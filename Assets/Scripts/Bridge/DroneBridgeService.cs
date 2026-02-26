using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// DroneBridgeService — DIE einzige API-Bridge fuer das gesamte Projekt.
///
/// Vereint Drohnen-Steuerung (Takeoff/Land/Move/Hover) mit der
/// Sensing-Pipeline (Sensor-Upload, Fusion-Trigger, Map-Pull, Health).
///
/// ═══════════════════════════════════════════════════════════════
/// UPLINK  (Unity -> APIs):
///   - Drohnen-Steuerung:   POST /takeoff, /land, /hover, /move, /reset
///   - NavData Polling:     GET  /navdata   (10 Hz)
///   - Status Polling:      GET  /status    (0.5 Hz)
///   - Sensor-Upload:       POST /analyze   (WiFi/CSI, 5 Hz)
///   - Sensor-Upload:       POST /depth/pointcloud (Image, 5 Hz)
///   - Fusion-Trigger:      POST /fuse      (1.5 Hz)
///
/// DOWNLINK (APIs -> Unity):
///   - Map-Pull:            GET  /map  +  /map/anomalies  (1 Hz)
///   - Health-Check:        GET  /health   (0.2 Hz)
///
/// ═══════════════════════════════════════════════════════════════
/// Stabilitaet:
///   - HTTP-Timeout 2-3s pro Request
///   - 1x Retry mit Backoff bei POST-Fehlern
///   - Sensor-Throttle wenn Fusion-Latenz > Schwelle
///   - Letzte Map gecacht (nie null nach erstem Empfang)
///
/// ═══════════════════════════════════════════════════════════════
/// Abhaengigkeiten:
///   - DroneBridgeConfig  (ScriptableObject — alle URLs, Raten, Parameter)
///   - SensorAdapter      (statische Payload-Builder + Koordinaten-Mapping)
///   - FusionMapRenderer  (empfaengt OnMapUpdated / OnAnomaliesUpdated)
///
/// Ersetzt:  DroneAPIBridge.cs  +  DroneApiBridgeExample.cs
/// </summary>
public class DroneBridgeService : MonoBehaviour
{
    // ══════════════════════════════════════════════════════════
    // Inspector
    // ══════════════════════════════════════════════════════════

    [Header("Configuration")]
    [Tooltip("ScriptableObject mit allen URLs, Raten und Parametern")]
    public DroneBridgeConfig config;

    [Header("References")]
    [Tooltip("Transform der Drohne (Position/Yaw fuer Fusion-Trigger)")]
    public Transform droneTransform;

    [Header("Drone Control Polling")]
    [Tooltip("NavData-Abfrageintervall in Sekunden (Standard: 0.1 = 10 Hz)")]
    public float navPollSec = 0.1f;
    [Tooltip("Status-Abfrageintervall in Sekunden")]
    public float statusPollSec = 0.5f;

    [Header("Sensing Pipeline")]
    [Tooltip("Sensing-Pipeline beim Start automatisch aktivieren")]
    public bool autoStartPipeline = true;

    // ══════════════════════════════════════════════════════════
    // Public State — Drone Control  (Kompatibel mit altem DroneAPIBridge)
    // ══════════════════════════════════════════════════════════

    /// <summary>Letzte NavData (Altitude, Battery, Velocity, Rotation, WiFi, …)</summary>
    [HideInInspector] public SensorAdapter.NavDataResponse nav = new SensorAdapter.NavDataResponse();

    /// <summary>Letzter Status (Connected, Flying, Emergency, Battery, …)</summary>
    [HideInInspector] public SensorAdapter.StatusResponse status = new SensorAdapter.StatusResponse();

    /// <summary>Letzte Bildqualitaet (Blur, Edge, Noise, Compression, …)</summary>
    [HideInInspector] public SensorAdapter.ImageQualityResponse imgQ = new SensorAdapter.ImageQualityResponse();

    /// <summary>API-Base-URL fuer Drohnensteuerung (wird aus config gelesen).</summary>
    public string apiBaseUrl => config != null ? config.simBase : "http://localhost:5050";

    // ══════════════════════════════════════════════════════════
    // Public State — Sensing Pipeline
    // ══════════════════════════════════════════════════════════

    /// <summary>True wenn alle 5 Services geantwortet haben.</summary>
    public bool ServicesReady { get; private set; }

    /// <summary>True wenn OSM-Map initialisiert wurde.</summary>
    public bool OsmReady { get; private set; }

    /// <summary>Letztes base64-Bild vom Simulator.</summary>
    public string LatestImageB64 { get; private set; }

    /// <summary>Letzte Fusion-Map (gecacht).</summary>
    public SensorAdapter.FusionMapResponse LatestMap { get; private set; }

    /// <summary>Letzte Anomalie-Liste.</summary>
    public SensorAdapter.AnomalyListResponse LatestAnomalies { get; private set; }

    // ══════════════════════════════════════════════════════════
    // Events
    // ══════════════════════════════════════════════════════════

    /// <summary>Feuert bei jedem NavData-Update.</summary>
    public event Action<SensorAdapter.NavDataResponse> OnNavdata;

    /// <summary>Feuert bei Bildqualitaets-Update.</summary>
    public event Action<SensorAdapter.ImageQualityResponse> OnImageQuality;

    /// <summary>Feuert wenn neue Map-Daten empfangen wurden.</summary>
    public event Action<SensorAdapter.FusionMapResponse> OnMapUpdated;

    /// <summary>Feuert wenn neue Anomalie-Daten empfangen wurden.</summary>
    public event Action<SensorAdapter.AnomalyListResponse> OnAnomaliesUpdated;

    /// <summary>Feuert wenn ein Service-Health-Check fehlschlaegt (name, url).</summary>
    public event Action<string, string> OnServiceDown;

    // ══════════════════════════════════════════════════════════
    // Internals
    // ══════════════════════════════════════════════════════════

    Dictionary<string, bool> _serviceHealth = new Dictionary<string, bool>();
    float _lastFusionLatency;
    bool _throttled;
    bool _pipelineRunning;

    // ══════════════════════════════════════════════════════════
    // Lifecycle
    // ══════════════════════════════════════════════════════════

    void OnEnable()
    {
        if (config == null)
        {
            Debug.LogError("[Bridge] DroneBridgeConfig fehlt! Im Inspector zuweisen.");
            enabled = false;
            return;
        }
    }

    void Start()
    {
        // Drone-Control Polling sofort starten (wie alter DroneAPIBridge)
        StartCoroutine(PollLoop("/navdata", navPollSec, s =>
        {
            nav = JsonConvert.DeserializeObject<SensorAdapter.NavDataResponse>(s);
            OnNavdata?.Invoke(nav);
        }));
        StartCoroutine(PollLoop("/status", statusPollSec, s =>
        {
            status = JsonConvert.DeserializeObject<SensorAdapter.StatusResponse>(s);
        }));

        // Sensing-Pipeline automatisch starten
        if (autoStartPipeline)
            StartPipeline();
    }

    // ══════════════════════════════════════════════════════════
    //  A)  DROHNEN-STEUERUNG  (ersetzt DroneAPIBridge)
    // ══════════════════════════════════════════════════════════

    /// <summary>Hebt ab.</summary>
    public void Takeoff() => StartCoroutine(PostEmpty("/takeoff"));

    /// <summary>Landet.</summary>
    public void Land() => StartCoroutine(PostEmpty("/land"));

    /// <summary>Schwebt (Nullkommando).</summary>
    public void Hover() => StartCoroutine(PostEmpty("/hover"));

    /// <summary>Setzt die Drohne zurueck (Not-Reset).</summary>
    public void ResetDrone() => StartCoroutine(PostEmpty("/reset"));

    /// <summary>
    /// Sendet einen Move-Befehl (AT*PCMD-style).
    /// Werte jeweils -1..1.
    /// </summary>
    public void Move(float fwd, float rt, float up, float turn)
    {
        var cmd = new SensorAdapter.MoveCommand
        {
            forward = Mathf.Clamp(fwd, -1f, 1f),
            right = Mathf.Clamp(rt, -1f, 1f),
            up = Mathf.Clamp(up, -1f, 1f),
            turn_speed = Mathf.Clamp(turn, -1f, 1f)
        };
        StartCoroutine(PostJsonRaw(apiBaseUrl + "/move", JsonConvert.SerializeObject(cmd)));
    }

    /// <summary>Fragt Bildqualitaet einmalig ab.</summary>
    public void FetchImageQuality()
    {
        StartCoroutine(GetOnce("/image/quality", s =>
        {
            imgQ = JsonConvert.DeserializeObject<SensorAdapter.ImageQualityResponse>(s);
            OnImageQuality?.Invoke(imgQ);
        }));
    }

    // ── Drone-Control HTTP Helpers ──

    IEnumerator PollLoop(string ep, float sec, Action<string> cb)
    {
        while (true)
        {
            using (var r = UnityWebRequest.Get(apiBaseUrl + ep))
            {
                r.timeout = config != null ? config.httpTimeoutSec : 2;
                yield return r.SendWebRequest();
                if (r.result == UnityWebRequest.Result.Success)
                    try { cb(r.downloadHandler.text); }
                    catch { }
            }
            yield return new WaitForSeconds(sec);
        }
    }

    IEnumerator GetOnce(string ep, Action<string> cb)
    {
        using (var r = UnityWebRequest.Get(apiBaseUrl + ep))
        {
            r.timeout = config != null ? config.httpTimeoutSec : 2;
            yield return r.SendWebRequest();
            if (r.result == UnityWebRequest.Result.Success)
                cb(r.downloadHandler.text);
        }
    }

    IEnumerator PostEmpty(string ep)
    {
        using (var r = UnityWebRequest.PostWwwForm(apiBaseUrl + ep, ""))
        {
            r.timeout = config != null ? config.httpTimeoutSec : 2;
            yield return r.SendWebRequest();
        }
    }

    IEnumerator PostJsonRaw(string url, string json)
    {
        using (var r = new UnityWebRequest(url, "POST"))
        {
            r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            r.downloadHandler = new DownloadHandlerBuffer();
            r.SetRequestHeader("Content-Type", "application/json");
            r.timeout = config != null ? config.httpTimeoutSec : 3;
            yield return r.SendWebRequest();
        }
    }

    // ══════════════════════════════════════════════════════════
    //  B)  SENSING-PIPELINE  (ersetzt DroneApiBridgeExample)
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Startet die Sensing-Pipeline (Service-Wait, OSM-Init, alle Loops).
    /// Wird automatisch aufgerufen wenn autoStartPipeline = true.
    /// Kann auch manuell von einem GameManager etc. aufgerufen werden.
    /// </summary>
    public void StartPipeline()
    {
        if (_pipelineRunning) return;
        _pipelineRunning = true;
        StartCoroutine(PipelineBootstrap());
    }

    IEnumerator PipelineBootstrap()
    {
        Debug.Log("[Bridge] Bootstrapping sensing pipeline...");

        yield return StartCoroutine(WaitForAllServices());
        ServicesReady = true;
        Debug.Log("[Bridge] Alle Services bereit.");

        yield return StartCoroutine(InitOsmMap());
        OsmReady = true;
        Debug.Log("[Bridge] OSM initialisiert.");

        StartCoroutine(SensorLoop());
        StartCoroutine(FusionLoop());
        StartCoroutine(MapLoop());
        StartCoroutine(HealthLoop());

        Debug.Log("[Bridge] Alle Loops laufen.");
    }

    // ── Service Discovery ──

    IEnumerator WaitForAllServices()
    {
        yield return StartCoroutine(WaitForService(config.simBase + "/status", "simulator"));
        yield return StartCoroutine(WaitForService(config.wifiBase + "/health", "wifi"));
        yield return StartCoroutine(WaitForService(config.depthBase + "/health", "depth"));
        yield return StartCoroutine(WaitForService(config.osmBase + "/health", "osm"));
        yield return StartCoroutine(WaitForService(config.fusionBase + "/health", "fusion"));
    }

    IEnumerator WaitForService(string url, string name)
    {
        float deadline = Time.realtimeSinceStartup + config.serviceBootTimeoutSec;
        while (Time.realtimeSinceStartup < deadline)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 2;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    _serviceHealth[name] = true;
                    Debug.Log($"[Bridge] Bereit: {name}");
                    yield break;
                }
            }
            yield return new WaitForSecondsRealtime(1f);
        }
        _serviceHealth[name] = false;
        Debug.LogError($"[Bridge] Timeout: {name} @ {url}");
    }

    // ── OSM Init ──

    IEnumerator InitOsmMap()
    {
        var payload = SensorAdapter.BuildOsmInit(config);
        yield return StartCoroutine(PostJsonPayload(config.osmBase + "/map/init", payload, _ =>
        {
            Debug.Log("[Bridge] OSM-Karte initialisiert.");
        }));
    }

    /// <summary>OSM-Karte neu initialisieren (z.B. bei Episode-Reset mit neuen Koordinaten).</summary>
    public void ReinitOsm(float lat, float lon, int radius)
    {
        var payload = new SensorAdapter.OsmInitPayload { lat = lat, lon = lon, radius = radius };
        StartCoroutine(PostJsonPayload(config.osmBase + "/map/init", payload, _ =>
        {
            Debug.Log("[Bridge] OSM-Karte re-initialisiert.");
        }));
    }

    // ── Sensor Loop (5 Hz) ──

    IEnumerator SensorLoop()
    {
        var wait = new WaitForSeconds(config.SensorInterval);
        while (true)
        {
            if (!ServicesReady || !OsmReady)
            {
                yield return wait;
                continue;
            }

            // Throttle bei langsamer Fusion
            if (_throttled)
            {
                yield return new WaitForSeconds(config.SensorInterval * 2f);
                _throttled = false;
                continue;
            }

            // 1) Image + Quality vom Simulator ziehen
            yield return StartCoroutine(PullImageAndQuality());

            // 2) Parallel an WiFi + Depth senden
            if (!string.IsNullOrEmpty(LatestImageB64) && nav != null)
            {
                yield return StartCoroutine(PushSensorData());
            }

            yield return wait;
        }
    }

    IEnumerator PullImageAndQuality()
    {
        // Image
        using (var req = UnityWebRequest.Get(config.simBase + "/image"))
        {
            req.timeout = config.httpTimeoutSec;
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var img = JsonConvert.DeserializeObject<SensorAdapter.ImageResponse>(
                        req.downloadHandler.text);
                    LatestImageB64 = img.data;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Bridge] Image parse: " + ex.Message);
                }
            }
        }

        // Quality (wird auch in imgQ gespeichert fuer ML-Agent-Kompatibilitaet)
        using (var req = UnityWebRequest.Get(config.simBase + "/image/quality"))
        {
            req.timeout = config.httpTimeoutSec;
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    imgQ = JsonConvert.DeserializeObject<SensorAdapter.ImageQualityResponse>(
                        req.downloadHandler.text);
                    OnImageQuality?.Invoke(imgQ);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Bridge] Quality parse: " + ex.Message);
                }
            }
        }
    }

    IEnumerator PushSensorData()
    {
        // Depth (fire & forget)
        var depthPayload = SensorAdapter.BuildDepthPayload(LatestImageB64);
        StartCoroutine(PostJsonPayload(config.depthBase + "/depth/pointcloud", depthPayload, _ => { }));

        // WiFi/CSI
        var wifiPayload = SensorAdapter.BuildWifiAnalyze(config, nav, imgQ);
        yield return StartCoroutine(PostJsonPayload(config.wifiBase + "/analyze", wifiPayload, _ => { }));
    }

    // ── Fusion Loop (1.5 Hz) ──

    IEnumerator FusionLoop()
    {
        var wait = new WaitForSeconds(config.FusionInterval);
        while (true)
        {
            if (nav != null && ServicesReady)
            {
                Vector3 uPos = droneTransform != null ? droneTransform.position : Vector3.zero;
                float yaw = droneTransform != null ? droneTransform.eulerAngles.y : nav.rotZ;

                var payload = SensorAdapter.BuildFusionTrigger(config, nav, uPos, yaw);

                float t0 = Time.realtimeSinceStartup;
                yield return StartCoroutine(PostJsonPayload(config.fusionBase + "/fuse", payload, _ =>
                {
                    _lastFusionLatency = Time.realtimeSinceStartup - t0;
                    _throttled = _lastFusionLatency > config.fusionLatencyThrottleSec;
                    if (_throttled)
                        Debug.LogWarning($"[Bridge] Fusion langsam ({_lastFusionLatency:F2}s) — throttle.");
                }));
            }
            yield return wait;
        }
    }

    // ── Map + Anomalies Loop (1 Hz) ──

    IEnumerator MapLoop()
    {
        var wait = new WaitForSeconds(config.MapInterval);
        while (true)
        {
            // Map
            using (var req = UnityWebRequest.Get(
                config.fusionBase + "/map?max_points=" + config.maxMapPoints))
            {
                req.timeout = config.httpTimeoutSec;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var map = JsonConvert.DeserializeObject<SensorAdapter.FusionMapResponse>(
                            req.downloadHandler.text);
                        LatestMap = map;
                        OnMapUpdated?.Invoke(map);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[Bridge] Map parse: " + ex.Message);
                    }
                }
            }

            // Anomalies
            using (var req = UnityWebRequest.Get(config.fusionBase + "/map/anomalies"))
            {
                req.timeout = config.httpTimeoutSec;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var anomalies = JsonConvert.DeserializeObject<SensorAdapter.AnomalyListResponse>(
                            req.downloadHandler.text);
                        LatestAnomalies = anomalies;
                        OnAnomaliesUpdated?.Invoke(anomalies);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[Bridge] Anomalies parse: " + ex.Message);
                    }
                }
            }

            yield return wait;
        }
    }

    // ── Health Loop (0.2 Hz = alle 5s) ──

    IEnumerator HealthLoop()
    {
        var wait = new WaitForSeconds(config.HealthInterval);
        while (true)
        {
            yield return StartCoroutine(PingService(config.wifiBase + "/health", "wifi"));
            yield return StartCoroutine(PingService(config.depthBase + "/health", "depth"));
            yield return StartCoroutine(PingService(config.osmBase + "/health", "osm"));
            yield return StartCoroutine(PingService(config.fusionBase + "/health", "fusion"));
            yield return wait;
        }
    }

    IEnumerator PingService(string url, string name)
    {
        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 2;
            yield return req.SendWebRequest();

            bool ok = req.result == UnityWebRequest.Result.Success;
            bool wasOk = _serviceHealth.ContainsKey(name) && _serviceHealth[name];
            _serviceHealth[name] = ok;

            if (!ok)
            {
                if (wasOk)
                    Debug.LogWarning($"[Bridge] Service DOWN: {name} @ {url}");
                OnServiceDown?.Invoke(name, url);
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  C)  PUBLIC API  (Status-Abfragen)
    // ══════════════════════════════════════════════════════════

    /// <summary>Prueft ob ein bestimmter Service gesund ist.</summary>
    public bool IsServiceHealthy(string name)
        => _serviceHealth.ContainsKey(name) && _serviceHealth[name];

    /// <summary>Letzte Fusion-Latenz in Sekunden.</summary>
    public float FusionLatency => _lastFusionLatency;

    /// <summary>True wenn Sensor-Uploads wegen langsamer Fusion gedrosselt werden.</summary>
    public bool IsThrottled => _throttled;

    /// <summary>Anzahl Fusion-Voxel (0 wenn noch keine Map).</summary>
    public int VoxelCount => LatestMap != null ? LatestMap.n_voxels : 0;

    /// <summary>Anzahl erkannter Anomalien.</summary>
    public int AnomalyCount =>
        LatestAnomalies?.anomalies != null ? LatestAnomalies.anomalies.Count : 0;

    // ══════════════════════════════════════════════════════════
    //  HTTP Helper — Generisch (Sensing-Pipeline, mit Retry)
    // ══════════════════════════════════════════════════════════

    IEnumerator PostJsonPayload<T>(string url, T payload, Action<string> onSuccess)
    {
        string json;
        try { json = JsonConvert.SerializeObject(payload); }
        catch (Exception ex)
        {
            Debug.LogWarning("[Bridge] Serialize: " + ex.Message);
            yield break;
        }

        var bodyRaw = Encoding.UTF8.GetBytes(json);

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = config.httpTimeoutSec;
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                onSuccess?.Invoke(req.downloadHandler.text);
            }
            else
            {
                // 1x Retry mit kurzem Backoff
                yield return new WaitForSeconds(0.3f);
                using (var retry = new UnityWebRequest(url, "POST"))
                {
                    retry.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    retry.downloadHandler = new DownloadHandlerBuffer();
                    retry.timeout = config.httpTimeoutSec;
                    retry.SetRequestHeader("Content-Type", "application/json");
                    yield return retry.SendWebRequest();

                    if (retry.result == UnityWebRequest.Result.Success)
                        onSuccess?.Invoke(retry.downloadHandler.text);
                    else
                        Debug.LogWarning($"[Bridge] POST {url} -> {retry.error}");
                }
            }
        }
    }
}
