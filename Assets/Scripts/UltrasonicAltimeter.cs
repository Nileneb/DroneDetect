using UnityEngine;

/// <summary>
/// Simuliert den Ultraschall-Hoehensonsor des Parrot AR.Drone 2.0.
/// Schiesst einen Raycast nach unten (entlang -transform.up) und misst
/// die Distanz zum Boden – genau wie der echte Sensor, der an der
/// Unterseite der Drohne montiert ist.
///
/// Features:
///   - Realistischer Messbereich (maxRange, default 6m wie beim echten Sensor)
///   - Sensor-Rauschen (Gaussian, konfigurierbar)
///   - Gibt -1 zurueck wenn kein Boden erkannt (ausserhalb Reichweite)
///   - LayerMask fuer selektive Erkennung (nur Terrain/Boden, nicht die Drohne selbst)
///   - Gizmo-Visualisierung im Editor
///
/// Montage: Auf das gleiche GameObject wie die anderen Sensoren (Gyro, Barometer etc.)
/// oder direkt auf den Drohnen-Body. Der Raycast geht von transform.position
/// entlang -transform.up nach unten.
/// </summary>
public class UltrasonicAltimeter : MonoBehaviour
{
    [Header("Sensor Settings")]
    [Tooltip("Maximale Messreichweite in Metern (echter AR.Drone: ~6m)")]
    public float maxRange = 6f;

    [Tooltip("Minimale Messreichweite in Metern (Totzone direkt unter Sensor)")]
    public float minRange = 0.02f;

    [Tooltip("Layers die als Boden erkannt werden (alles ausser Drohne selbst)")]
    public LayerMask groundLayers = ~0; // default: alles

    [Header("Noise")]
    [Tooltip("Standardabweichung des Gauss-Rauschens in Metern")]
    public float noiseSigma = 0.02f;

    [Tooltip("Rauschen aktivieren (fuer realistischeres Training)")]
    public bool noiseEnabled = true;

    [Header("Debug")]
    [Tooltip("Raycast im Scene-View anzeigen")]
    public bool drawDebugRay = true;

    // ──────────────── Messwerte (oeffentlich lesbar) ────────────────

    /// <summary>
    /// Letzte gemessene Hoehe ueber Grund in Metern.
    /// -1 wenn kein Boden in Reichweite.
    /// </summary>
    public float Altitude { get; private set; }

    /// <summary>
    /// Rohe Hoehe ohne Rauschen (fuer Debug/Vergleich).
    /// </summary>
    public float AltitudeRaw { get; private set; }

    /// <summary>
    /// True wenn der Sensor aktuell einen Boden erkennt.
    /// </summary>
    public bool HasGround { get; private set; }

    /// <summary>
    /// Vertikale Geschwindigkeit (aus Altimeter-Differenz, wie beim echten Sensor).
    /// Positiv = steigend, Negativ = sinkend.
    /// </summary>
    public float VerticalSpeed { get; private set; }

    /// <summary>
    /// Der RaycastHit des letzten Messvorgangs (fuer Bodennormale etc.)
    /// </summary>
    public RaycastHit LastHit { get; private set; }

    // ──────────────── Privat ────────────────
    float prevAltitude;
    bool prevHadGround;

    void Awake()
    {
        Altitude = -1f;
        AltitudeRaw = -1f;
        HasGround = false;
        VerticalSpeed = 0f;
        prevAltitude = 0f;
        prevHadGround = false;
    }

    void FixedUpdate()
    {
        Measure();
    }

    /// <summary>
    /// Fuehrt eine Messung durch. Wird automatisch in FixedUpdate aufgerufen,
    /// kann aber auch manuell getriggert werden.
    /// </summary>
    public void Measure()
    {
        // Raycast entlang der lokalen "unten"-Richtung der Drohne
        // (bei gekippter Drohne ist das nicht global down – realistisch!)
        Ray ray = new Ray(transform.position, -transform.up);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit, maxRange, groundLayers, QueryTriggerInteraction.Ignore))
        {
            float rawDist = hit.distance;

            if (rawDist < minRange)
            {
                // Totzone – zu nah fuer Messung
                AltitudeRaw = 0f;
                Altitude = 0f;
                HasGround = true;
            }
            else
            {
                AltitudeRaw = rawDist;
                Altitude = noiseEnabled ? rawDist + GaussNoise() : rawDist;
                Altitude = Mathf.Max(0f, Altitude); // kein negativer Wert
                HasGround = true;
            }

            LastHit = hit;
        }
        else
        {
            // Kein Boden in Reichweite
            AltitudeRaw = -1f;
            Altitude = -1f;
            HasGround = false;
        }

        // Vertikale Geschwindigkeit berechnen
        if (HasGround && prevHadGround && prevAltitude >= 0f && Altitude >= 0f)
        {
            VerticalSpeed = (Altitude - prevAltitude) / Time.fixedDeltaTime;
        }
        else
        {
            VerticalSpeed = 0f;
        }

        prevAltitude = Altitude;
        prevHadGround = HasGround;

        // Debug-Visualisierung
        if (drawDebugRay)
        {
            float drawLen = HasGround ? AltitudeRaw : maxRange;
            Color col = HasGround ? Color.cyan : Color.red;
            Debug.DrawRay(transform.position, -transform.up * drawLen, col);
        }
    }

    /// <summary>
    /// Erzeugt Gauss-verteiltes Rauschen (Box-Muller-Transformation).
    /// </summary>
    float GaussNoise()
    {
        // Box-Muller
        float u1 = 1f - Random.value; // (0,1]
        float u2 = Random.value;
        float z = Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        return z * noiseSigma;
    }

    /// <summary>
    /// Setzt den Sensor zurueck (z.B. bei Episode-Reset).
    /// </summary>
    public void Reset()
    {
        Altitude = -1f;
        AltitudeRaw = -1f;
        HasGround = false;
        VerticalSpeed = 0f;
        prevAltitude = 0f;
        prevHadGround = false;
    }

    // ──────────────── Gizmos ────────────────
    void OnDrawGizmosSelected()
    {
        // Messbereich als Linie + Kegel anzeigen
        Gizmos.color = new Color(0f, 1f, 1f, 0.5f);
        Vector3 from = transform.position;
        Vector3 to = from + (-transform.up) * maxRange;
        Gizmos.DrawLine(from, to);
        Gizmos.DrawWireSphere(to, 0.1f);

        // Aktueller Messwert
        if (Application.isPlaying && HasGround)
        {
            Gizmos.color = Color.green;
            Vector3 hitPoint = from + (-transform.up) * AltitudeRaw;
            Gizmos.DrawSphere(hitPoint, 0.08f);
        }
    }
}
