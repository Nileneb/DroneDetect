using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Text;

/// <summary>
/// WifiSignalSimulator — Physikalisch fundierte WiFi/CSI-Simulation fuer Unity.
///
/// Modelliert:
///   1) Log-Distance Path Loss (RSSI)
///   2) Rician Multipath Fading
///   3) CSI-Subcarrier Amplituden + Phasen (802.11n, 52 subcarriers)
///   4) Glasfaserkabel-Stoerung (EMI auf WiFi-Signal)
///   5) Temporal Coherence + Domain Randomization
///
/// Auf Drohne legen. APs als Transforms zuweisen.
/// Kabel-GameObjects: Tag "FiberCable" oder manuell in cableSources.
/// DroneMLAgent liest Features via AddObservationsToSensor().
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class WifiSignalSimulator : MonoBehaviour
{
    [Header("WiFi System")]
    public float txPowerDbm = 17f;
    public float frequencyGHz = 2.437f;         // Channel 6
    public float referenceDistanceM = 1.0f;
    public float pathLossAtD0Db = 40.0f;
    public float pathLossExponent = 2.8f;        // 2.0=free, 2.7-3.5=indoor
    public float shadowFadingSigmaDb = 5.0f;

    [Header("Fading")]
    public float ricianKFactorDb = 6.0f;         // 0=Rayleigh, 6-10=strong LoS
    public float fadingUpdateHz = 10f;
    [Range(0f, 0.999f)] public float fadingCorrelation = 0.85f;

    [Header("CSI")]
    public int numSubcarriers = 52;              // 802.11n 20MHz
    public float subcarrierSpacingKHz = 312.5f;  // 802.11 standard
    public int maxMultipathRays = 5;

    [Header("Kabel-Stoerung")]
    public float cableMaxRadius = 3.0f;
    public float cableMaxNoiseDb = 15.0f;
    public float cablePhaseJitterRad = 0.8f;
    public bool cableFrequencySelective = true;

    [Header("References")]
    public Transform[] accessPoints;
    public Transform[] cableSources;

    [Header("Network")]
    [Tooltip("Referenz auf DroneBridgeService (fuer drone_id und apiBaseUrl)")]
    public DroneBridgeService api;
    [Tooltip("Sendeintervall an Bodenstation in Sekunden (0 = deaktiviert)")]
    public float sendIntervalSec = 0.5f;

    [Header("Debug")]
    public bool showDebugGizmos = true;
    public bool logValues = false;

    // --- Public Readouts ---
    [NonSerialized] public float rssiDbm;
    [NonSerialized] public float signalNormalized;
    [NonSerialized] public float snrDb;
    [NonSerialized] public float[] csiAmplitudes;
    [NonSerialized] public float[] csiPhases;
    [NonSerialized] public float csiMeanAmplitude;
    [NonSerialized] public float csiAmplitudeVariance;
    [NonSerialized] public float csiPhaseSpread;
    [NonSerialized] public float cableInterference;
    [NonSerialized] public float subcarrierCorrelation;
    [NonSerialized] public float[] featureVector = new float[8];

    // --- Internal ---
    float[] _fadingState, _shadowState;
    float _fadingTimer, _shadowTimer;
    float[][] _mpDelays, _mpAmplitudes, _mpPhases;
    System.Random _rng;
    float _sendTimer;

    void Awake()
    {
        _rng = new System.Random(UnityEngine.Random.Range(0, int.MaxValue));

        if (accessPoints == null || accessPoints.Length == 0)
        {
            var apGO = new GameObject("DefaultAP");
            apGO.transform.position = new Vector3(0f, 3f, 0f);
            accessPoints = new Transform[] { apGO.transform };
        }

        if (cableSources == null || cableSources.Length == 0)
        {
            try
            {
                var cables = GameObject.FindGameObjectsWithTag("FiberCable");
                if (cables.Length > 0)
                {
                    cableSources = new Transform[cables.Length];
                    for (int i = 0; i < cables.Length; i++)
                        cableSources[i] = cables[i].transform;
                }
            }
            catch { /* Tag not defined — ok */ }
        }

        InitState();
    }

    void InitState()
    {
        int n = accessPoints.Length;
        _fadingState = new float[n];
        _shadowState = new float[n];
        csiAmplitudes = new float[numSubcarriers];
        csiPhases = new float[numSubcarriers];
        _mpDelays = new float[n][];
        _mpAmplitudes = new float[n][];
        _mpPhases = new float[n][];
        for (int ap = 0; ap < n; ap++)
        {
            _mpDelays[ap] = new float[maxMultipathRays];
            _mpAmplitudes[ap] = new float[maxMultipathRays];
            _mpPhases[ap] = new float[maxMultipathRays];
            RegenMultipath(ap);
        }
    }

    void FixedUpdate()
    {
        UpdateFading(Time.fixedDeltaTime);
        CalcRSSI();
        CalcCSI();
        CalcCableInterference();
        ApplyCableToCSI();
        CalcFeatures();

        // CSI-Daten an Bodenstation senden
        if (sendIntervalSec > 0f)
        {
            _sendTimer += Time.fixedDeltaTime;
            if (_sendTimer >= sendIntervalSec)
            {
                _sendTimer = 0f;
                SendToGroundStation();
            }
        }

        if (logValues && Time.frameCount % 50 == 0)
            Debug.Log($"[Wifi] RSSI={rssiDbm:F1} SNR={snrDb:F1} " +
                      $"Cable={cableInterference:F3} CSIvar={csiAmplitudeVariance:F4}");
    }

    // ════════════ RSSI: Log-Distance Path Loss ════════════

    void CalcRSSI()
    {
        float best = -120f;
        Vector3 pos = transform.position;
        for (int ap = 0; ap < accessPoints.Length; ap++)
        {
            if (accessPoints[ap] == null) continue;
            float d = Mathf.Max(0.1f, Vector3.Distance(pos, accessPoints[ap].position));
            float pl = pathLossAtD0Db
                     + 10f * pathLossExponent * Mathf.Log10(d / referenceDistanceM)
                     + _shadowState[ap];
            float r = Mathf.Clamp(txPowerDbm - pl + _fadingState[ap], -120f, 0f);
            if (r > best) best = r;
        }
        rssiDbm = best;
        signalNormalized = Mathf.Clamp01((rssiDbm + 90f) / 60f);
        snrDb = rssiDbm + 95f; // noise floor = -95dBm
    }

    // ════════════ Rician Fading + Shadow Fading ════════════

    void UpdateFading(float dt)
    {
        _fadingTimer += dt;
        _shadowTimer += dt;
        if (_fadingTimer < 1f / Mathf.Max(1f, fadingUpdateHz)) return;
        _fadingTimer = 0f;

        float K = Mathf.Pow(10f, ricianKFactorDb / 10f);
        float los = Mathf.Sqrt(K / (K + 1f));
        float nlos = Mathf.Sqrt(1f / (K + 1f));

        for (int ap = 0; ap < accessPoints.Length; ap++)
        {
            float hR = los + nlos * (float)Gauss();
            float hI = nlos * (float)Gauss();
            float fDb = 10f * Mathf.Log10(Mathf.Max(hR * hR + hI * hI, 1e-6f));

            // Temporal correlation for smooth fading
            _fadingState[ap] = fadingCorrelation * _fadingState[ap]
                             + (1f - fadingCorrelation) * fDb;

            // Shadow fading updates slowly (~every 2s)
            if (_shadowTimer >= 2f)
            {
                _shadowState[ap] = 0.95f * _shadowState[ap]
                                 + 0.05f * (float)Gauss() * shadowFadingSigmaDb;
                RegenMultipath(ap);
            }
        }
        if (_shadowTimer >= 2f) _shadowTimer = 0f;
    }

    // ════════════ CSI: H(f_k) = Σ a_l·e^(-j2πf_k·τ_l) ════════════

    void CalcCSI()
    {
        float cf = frequencyGHz * 1e9f;
        float sp = subcarrierSpacingKHz * 1e3f;
        float sf = cf - (numSubcarriers / 2f) * sp;

        for (int k = 0; k < numSubcarriers; k++)
        {
            csiAmplitudes[k] = 0f;
            csiPhases[k] = 0f;
        }

        Vector3 pos = transform.position;
        for (int ap = 0; ap < accessPoints.Length; ap++)
        {
            if (accessPoints[ap] == null) continue;
            float dist = Vector3.Distance(pos, accessPoints[ap].position);

            for (int k = 0; k < numSubcarriers; k++)
            {
                float fk = sf + k * sp;
                float hR = 0f, hI = 0f;

                for (int l = 0; l < maxMultipathRays; l++)
                {
                    float a = _mpAmplitudes[ap][l];
                    float tau = _mpDelays[ap][l] + dist / 3e8f;
                    float phi = -2f * Mathf.PI * fk * tau + _mpPhases[ap][l];
                    hR += a * Mathf.Cos(phi);
                    hI += a * Mathf.Sin(phi);
                }

                csiAmplitudes[k] += Mathf.Sqrt(hR * hR + hI * hI);
                csiPhases[k] = Mathf.Atan2(hI, hR);
            }
        }
    }

    // ════════════ Cable EMI Effects ════════════

    void CalcCableInterference()
    {
        cableInterference = 0f;
        if (cableSources == null) return;
        Vector3 pos = transform.position;
        foreach (var c in cableSources)
        {
            if (c == null) continue;
            float d = Vector3.Distance(pos, c.position);
            if (d >= cableMaxRadius) continue;
            // Inverse-square falloff (more physical than linear)
            float e = Mathf.Pow(1f - d / cableMaxRadius, 2f);
            if (e > cableInterference) cableInterference = e;
        }
    }

    void ApplyCableToCSI()
    {
        if (cableInterference < 0.01f) return;

        float nDb = cableInterference * cableMaxNoiseDb;
        float nLin = Mathf.Pow(10f, nDb / 20f);

        rssiDbm -= nDb * 0.5f;
        signalNormalized = Mathf.Clamp01((rssiDbm + 90f) / 60f);
        snrDb -= nDb;

        for (int k = 0; k < numSubcarriers; k++)
        {
            // 1) Broadband noise
            csiAmplitudes[k] = Mathf.Max(0.001f,
                csiAmplitudes[k] + (float)Gauss() * nLin * 0.3f);

            // 2) Frequency-selective notches (cable resonances)
            if (cableFrequencySelective)
            {
                float res = Mathf.Abs(Mathf.Sin(k * 0.3f + cableInterference * 5f));
                if (res > 0.8f)
                    csiAmplitudes[k] *= (1f - cableInterference * res * 0.7f);
            }

            // 3) Phase jitter
            csiPhases[k] += (float)Gauss() * cablePhaseJitterRad * cableInterference;
            csiPhases[k] = Mathf.Repeat(csiPhases[k] + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
        }
    }

    // ════════════ Feature Extraction (8 floats) ════════════

    void CalcFeatures()
    {
        float sA = 0, sA2 = 0, sP = 0, sP2 = 0, corr = 0;
        float prev = csiAmplitudes[0];

        for (int k = 0; k < numSubcarriers; k++)
        {
            float a = csiAmplitudes[k], p = csiPhases[k];
            sA += a; sA2 += a * a; sP += p; sP2 += p * p;
            if (k > 0) { corr += Mathf.Abs(a - prev); prev = a; }
        }

        int N = numSubcarriers;
        csiMeanAmplitude = sA / N;
        csiAmplitudeVariance = Mathf.Max(0, sA2 / N - csiMeanAmplitude * csiMeanAmplitude);
        float mP = sP / N;
        csiPhaseSpread = Mathf.Sqrt(Mathf.Max(0, sP2 / N - mP * mP));
        subcarrierCorrelation = 1f - Mathf.Clamp01(corr / (N * csiMeanAmplitude + 0.001f));

        // Same semantics as real CSI detector measurements
        featureVector[0] = signalNormalized;                            // signal_strength
        featureVector[1] = Mathf.Clamp01((snrDb + 10f) / 50f);         // snr
        featureVector[2] = Mathf.Clamp01(csiMeanAmplitude / 2f);       // csi_amp_mean
        featureVector[3] = Mathf.Clamp01(csiAmplitudeVariance);         // csi_amp_var
        featureVector[4] = Mathf.Clamp01(csiPhaseSpread / Mathf.PI);    // phase_spread
        featureVector[5] = subcarrierCorrelation;                       // subcarrier_corr
        featureVector[6] = cableInterference;                           // cable_proximity (GT)
        featureVector[7] = Mathf.Clamp01(                               // detection_score
            0.30f * (1f - featureVector[0])
          + 0.20f * featureVector[3]
          + 0.25f * featureVector[4]
          + 0.25f * (1f - featureVector[5]));
    }

    // ════════════ Network: Send to Ground Station ════════════

    [Serializable] public class Vec3 { public float x, y, z; }

    [Serializable]
    public class WifiIngestPayload
    {
        public string drone_id;
        public double timestamp;
        public Vec3 position;
        public float[] features;
        public string source;
    }

    [Serializable]
    public class WifiIngestResponse
    {
        public float detection_score;
        public bool is_anomaly;
        public float confidence;
    }

    void SendToGroundStation()
    {
        var payload = new WifiIngestPayload
        {
            drone_id = api != null ? "parrot01" : "sim01",
            timestamp = Time.timeAsDouble,
            position = new Vec3
            {
                x = transform.position.x,
                y = transform.position.y,
                z = transform.position.z
            },
            features = featureVector,
            source = Application.isEditor ? "sim" : "real"
        };

        string json = JsonUtility.ToJson(payload);
        StartCoroutine(PostCSI(json));
    }

    IEnumerator PostCSI(string json)
    {
        // URL: leite aus apiBaseUrl den wifi-sensing Port ab (:6060)
        string baseUrl = api != null ? api.apiBaseUrl : "http://localhost:6060";
        string url = baseUrl.Replace(":5050", ":6060")
                            .Replace(":5051", ":6060")
                     + "/ingest";

        var req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 2;
        yield return req.SendWebRequest();

        if (logValues && req.result == UnityWebRequest.Result.Success)
        {
            var resp = JsonUtility.FromJson<WifiIngestResponse>(req.downloadHandler.text);
            Debug.Log($"[WiFi→GS] score={resp.detection_score:F2} anomaly={resp.is_anomaly}");
        }

        req.Dispose();
    }

    // ════════════ Multipath Generation ════════════

    void RegenMultipath(int ap)
    {
        // LoS path (strongest, zero delay)
        _mpDelays[ap][0] = 0f;
        _mpAmplitudes[ap][0] = 1.0f;
        _mpPhases[ap][0] = (float)(_rng.NextDouble() * 2.0 * Math.PI);

        // Reflection paths (exponentially decaying)
        for (int l = 1; l < maxMultipathRays; l++)
        {
            _mpDelays[ap][l] = (float)(l * 20e-9 + _rng.NextDouble() * 50e-9);
            _mpAmplitudes[ap][l] = Mathf.Pow(0.5f, l)
                                 * (0.5f + (float)_rng.NextDouble() * 0.5f);
            _mpPhases[ap][l] = (float)(_rng.NextDouble() * 2.0 * Math.PI);
        }
    }

    // ════════════ Gaussian RNG (Box-Muller) ════════════

    double _sp; bool _hs;
    double Gauss()
    {
        if (_hs) { _hs = false; return _sp; }
        double u, v, s;
        do
        {
            u = _rng.NextDouble() * 2 - 1; v = _rng.NextDouble() * 2 - 1;
            s = u * u + v * v;
        }
        while (s >= 1 || s == 0);
        s = Math.Sqrt(-2 * Math.Log(s) / s);
        _sp = v * s; _hs = true;
        return u * s;
    }

    // ════════════ Public API for DroneMLAgent ════════════

    /// <summary>Add 8 WiFi observations to Agent's VectorSensor</summary>
    public void AddObservationsToSensor(Unity.MLAgents.Sensors.VectorSensor sensor)
    {
        for (int i = 0; i < featureVector.Length; i++)
            sensor.AddObservation(featureVector[i]);
    }

    /// <summary>Reward signal: cable proximity 0..1</summary>
    public float GetCableProximityReward() => cableInterference;

    /// <summary>Is cable detected? (threshold-based)</summary>
    public bool IsCableDetected(float threshold = 0.3f) => featureVector[7] >= threshold;

    /// <summary>Call in OnEpisodeBegin()</summary>
    public void ResetForEpisode()
    {
        for (int ap = 0; ap < accessPoints.Length; ap++)
        {
            _fadingState[ap] = 0f; _shadowState[ap] = 0f;
            RegenMultipath(ap);
        }
        _fadingTimer = _shadowTimer = 0f;
        cableInterference = 0f;
    }

    /// <summary>Domain Randomization — call in OnEpisodeBegin()</summary>
    public void RandomizeParameters()
    {
        pathLossExponent = UnityEngine.Random.Range(2.0f, 4.5f);
        shadowFadingSigmaDb = UnityEngine.Random.Range(2f, 8f);
        ricianKFactorDb = UnityEngine.Random.Range(0f, 12f);
        cableMaxNoiseDb = UnityEngine.Random.Range(5f, 25f);
        cablePhaseJitterRad = UnityEngine.Random.Range(0.3f, 1.5f);
        cableMaxRadius = UnityEngine.Random.Range(1.5f, 5f);
    }

    // ════════════ Gizmos ════════════

    void OnDrawGizmosSelected()
    {
        if (!showDebugGizmos) return;
        if (accessPoints != null)
        {
            Gizmos.color = Color.cyan;
            foreach (var ap in accessPoints)
            {
                if (ap == null) continue;
                Gizmos.DrawWireSphere(ap.position, 0.3f);
                Gizmos.DrawLine(transform.position, ap.position);
            }
        }
        if (cableSources != null)
        {
            foreach (var c in cableSources)
            {
                if (c == null) continue;
                bool inR = Vector3.Distance(transform.position, c.position) < cableMaxRadius;
                Gizmos.color = inR ? Color.red : Color.yellow;
                Gizmos.DrawWireSphere(c.position, cableMaxRadius);
                Gizmos.color = new Color(1f, 0.5f, 0f, 0.8f);
                Gizmos.DrawLine(c.position + Vector3.left * 5f,
                                c.position + Vector3.right * 5f);
            }
        }
        if (Application.isPlaying)
        {
            Gizmos.color = Color.Lerp(Color.red, Color.green, signalNormalized);
            Gizmos.DrawSphere(transform.position + Vector3.up * 0.5f, 0.15f);
            if (cableInterference > 0.1f)
            {
                Gizmos.color = new Color(1f, 0f, 0f, cableInterference);
                Gizmos.DrawSphere(transform.position + Vector3.up * 0.8f, 0.1f);
            }
        }
    }
}
