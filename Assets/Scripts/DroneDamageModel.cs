using UnityEngine;
using System;

/// <summary>
/// Lokalisiertes Schadensmodell fuer die Drohne.
/// 
/// Erkennt Kollisionen und ordnet den Schaden der spezifischen Stelle zu,
/// an der die Drohne aufgeschlagen ist. Das 3D-Modell wird in Zonen eingeteilt:
///
///   - 4 Propeller/Rotoren (V1, V2, O1, O2) – Schaden reduziert Schub
///   - Body oben / Body unten – struktureller Schaden
///   - Front (Kamera/Sensoren) – Sensor-Degradation
///
/// Integration mit droneMovementController:
///   Beschaedigte Propeller bekommen einen Leistungsmultiplikator (0..1),
///   der die maximale Rotorkraft reduziert. Das fuehrt zu asymmetrischem
///   Schub und damit zu Instabilitaet – realistisch!
///
/// Integration mit DroneMLAgent:
///   - TotalDamageNormalized liefert Gesamt-Schadenswert fuer Observations
///   - OnDamageReceived Event fuer Reward-Strafen
///   - IsCritical fuer Episode-Abbruch bei zu viel Schaden
///
/// Montage: Auf das Root-GameObject der Drohne (gleiche Ebene wie Rigidbody).
/// Die Rotor-Transforms muessen im Inspector zugewiesen werden.
/// </summary>
public class DroneDamageModel : MonoBehaviour
{
    // ──────────────────────── Zonen-Definition ────────────────────────

    [Serializable]
    public class DamageZone
    {
        public string name;
        [Tooltip("Aktueller Schaden dieser Zone (0 = heil, 1 = zerstoert)")]
        [Range(0f, 1f)]
        public float damage;
        [Tooltip("Wie empfindlich diese Zone gegenueber Aufprall ist (Schaden pro m/s Aufprallgeschwindigkeit)")]
        public float sensitivity = 0.15f;
        [Tooltip("Maximaler Schaden pro einzelnem Aufprall")]
        public float maxDamagePerHit = 0.4f;

        public DamageZone(string name, float sensitivity = 0.15f)
        {
            this.name = name;
            this.damage = 0f;
            this.sensitivity = sensitivity;
            this.maxDamagePerHit = 0.4f;
        }
    }

    // ──────────────────────── Rotor-Referenzen ────────────────────────

    [Header("Rotor Transforms (fuer Zonen-Zuordnung)")]
    [Tooltip("Transform des Rotors V1 (vorne-links)")]
    public Transform rotorV1Transform;
    [Tooltip("Transform des Rotors V2 (hinten-rechts)")]
    public Transform rotorV2Transform;
    [Tooltip("Transform des Rotors O1 (vorne-rechts)")]
    public Transform rotorO1Transform;
    [Tooltip("Transform des Rotors O2 (hinten-links)")]
    public Transform rotorO2Transform;

    [Header("Flight Controller (fuer Schub-Reduktion)")]
    public droneMovementController flightController;

    // ──────────────────────── Schaden-Zonen ────────────────────────

    [Header("Damage Zones")]
    public DamageZone propV1 = new DamageZone("Propeller V1", 0.20f);
    public DamageZone propV2 = new DamageZone("Propeller V2", 0.20f);
    public DamageZone propO1 = new DamageZone("Propeller O1", 0.20f);
    public DamageZone propO2 = new DamageZone("Propeller O2", 0.20f);
    public DamageZone bodyTop = new DamageZone("Body Top", 0.10f);
    public DamageZone bodyBottom = new DamageZone("Body Bottom", 0.12f);
    public DamageZone front = new DamageZone("Front (Sensoren)", 0.18f);

    // ──────────────────────── Settings ────────────────────────

    [Header("Settings")]
    [Tooltip("Mindest-Aufprallgeschwindigkeit (m/s) ab der Schaden entsteht")]
    public float minImpactSpeed = 1.5f;

    [Tooltip("Gesamtschaden ab dem die Drohne als 'kritisch beschaedigt' gilt")]
    [Range(0f, 1f)]
    public float criticalDamageThreshold = 0.7f;

    [Tooltip("Radius um einen Rotor-Transform innerhalb dessen ein Treffer als Propeller-Schaden zaehlt")]
    public float propellerHitRadius = 0.15f;

    [Tooltip("Schaden repariert sich langsam (Selbstheilung pro Sekunde, 0 = aus)")]
    public float autoRepairRate = 0f;

    // ──────────────────────── Events ────────────────────────

    /// <summary>
    /// Wird bei jedem Schadensevent gefeuert.
    /// Parameter: (Zonenname, Schaden dieser Zone, Impact-Geschwindigkeit)
    /// </summary>
    public event Action<string, float, float> OnDamageReceived;

    /// <summary>
    /// Wird gefeuert wenn der Gesamtschaden criticalDamageThreshold ueberschreitet.
    /// </summary>
    public event Action OnCriticalDamage;

    // ──────────────────────── Oeffentliche Properties ────────────────────────

    /// <summary>Gesamtschaden normalisiert (0..1), Durchschnitt aller Zonen.</summary>
    public float TotalDamageNormalized
    {
        get
        {
            return (propV1.damage + propV2.damage + propO1.damage + propO2.damage
                  + bodyTop.damage + bodyBottom.damage + front.damage) / 7f;
        }
    }

    /// <summary>Maximaler Einzelzonen-Schaden (0..1).</summary>
    public float MaxZoneDamage
    {
        get
        {
            return Mathf.Max(propV1.damage,
                   Mathf.Max(propV2.damage,
                   Mathf.Max(propO1.damage,
                   Mathf.Max(propO2.damage,
                   Mathf.Max(bodyTop.damage,
                   Mathf.Max(bodyBottom.damage, front.damage))))));
        }
    }

    /// <summary>True wenn Gesamtschaden ueber criticalDamageThreshold.</summary>
    public bool IsCritical => TotalDamageNormalized >= criticalDamageThreshold;

    /// <summary>Propeller-Schadenswerte als Array [V1, V2, O1, O2] fuer Observations.</summary>
    public float[] PropellerDamages => new float[]
        { propV1.damage, propV2.damage, propO1.damage, propO2.damage };

    /// <summary>Durchschnittlicher Propeller-Schaden (0..1).</summary>
    public float AveragePropellerDamage =>
        (propV1.damage + propV2.damage + propO1.damage + propO2.damage) / 4f;

    // ──────────────────────── Privat ────────────────────────

    Rigidbody rb;
    DamageZone[] allZones;
    bool criticalFired;

    void Awake()
    {
        rb = GetComponentInParent<Rigidbody>();
        if (rb == null) rb = GetComponent<Rigidbody>();

        allZones = new DamageZone[]
            { propV1, propV2, propO1, propO2, bodyTop, bodyBottom, front };
    }

    void FixedUpdate()
    {
        // Auto-Repair
        if (autoRepairRate > 0f)
        {
            float repair = autoRepairRate * Time.fixedDeltaTime;
            foreach (var z in allZones)
                z.damage = Mathf.Max(0f, z.damage - repair);
        }

        // Propeller-Schaden auf Rotorleistung anwenden
        ApplyPropellerDamage();
    }

    // ──────────────────────── Kollisions-Erkennung ────────────────────────

    void OnCollisionEnter(Collision collision)
    {
        if (collision.relativeVelocity.magnitude < minImpactSpeed) return;

        float impactSpeed = collision.relativeVelocity.magnitude;

        // Jeden Kontaktpunkt auswerten
        foreach (ContactPoint contact in collision.contacts)
        {
            DamageZone zone = ClassifyHitZone(contact.point, contact.normal);
            float rawDamage = impactSpeed * zone.sensitivity;
            float clampedDamage = Mathf.Min(rawDamage, zone.maxDamagePerHit);

            zone.damage = Mathf.Clamp01(zone.damage + clampedDamage);

            OnDamageReceived?.Invoke(zone.name, clampedDamage, impactSpeed);
        }

        // Kritischer Schaden pruefen
        if (!criticalFired && IsCritical)
        {
            criticalFired = true;
            OnCriticalDamage?.Invoke();
        }
    }

    // ──────────────────────── Zonen-Klassifikation ────────────────────────

    /// <summary>
    /// Ordnet einen Kollisions-Kontaktpunkt einer Schadenszone zu.
    /// Prioritaet: Propeller (naechster) > Front > Body Top/Bottom.
    /// </summary>
    DamageZone ClassifyHitZone(Vector3 worldPoint, Vector3 normal)
    {
        // 1) Pruefen ob ein Propeller getroffen wurde (naechster Rotor)
        float minRotorDist = float.MaxValue;
        int closestRotor = -1;

        Transform[] rotorTransforms = { rotorV1Transform, rotorV2Transform,
                                         rotorO1Transform, rotorO2Transform };
        DamageZone[] rotorZones = { propV1, propV2, propO1, propO2 };

        for (int i = 0; i < rotorTransforms.Length; i++)
        {
            if (rotorTransforms[i] == null) continue;
            float d = Vector3.Distance(worldPoint, rotorTransforms[i].position);
            if (d < minRotorDist)
            {
                minRotorDist = d;
                closestRotor = i;
            }
        }

        if (closestRotor >= 0 && minRotorDist < propellerHitRadius)
        {
            return rotorZones[closestRotor];
        }

        // 2) Lokale Position des Aufprallpunkts relativ zur Drohne
        Vector3 localHit = transform.InverseTransformPoint(worldPoint);

        // Front: vor der Drohne (lokales Z > 0, innerhalb mittlerer Hoehe)
        if (localHit.z > 0.05f && Mathf.Abs(localHit.y) < 0.1f)
        {
            return front;
        }

        // Top vs Bottom: basierend auf lokaler Y-Position
        if (localHit.y > 0f)
        {
            return bodyTop;
        }
        else
        {
            return bodyBottom;
        }
    }

    // ──────────────────────── Propeller-Schaden anwenden ────────────────────────

    /// <summary>
    /// Reduziert die Rotorleistung basierend auf Propeller-Schaden.
    /// Ein beschaedigter Propeller liefert weniger Schub (Multiplikator = 1 - damage).
    /// Dies erzeugt realistisch asymmetrischen Schub!
    /// </summary>
    void ApplyPropellerDamage()
    {
        if (flightController == null) return;

        // Rotor-Power nach PID-Berechnung skalieren
        // (wird NACH dem FixedUpdate des flightControllers angewendet,
        //  da dieser Script eine niedrigere Execution Order haben sollte)
        if (flightController.helixV1 != null)
            flightController.helixV1.setPower(
                flightController.helixV1.getPower() * (1f - propV1.damage));

        if (flightController.helixV2 != null)
            flightController.helixV2.setPower(
                flightController.helixV2.getPower() * (1f - propV2.damage));

        if (flightController.helixO1 != null)
            flightController.helixO1.setPower(
                flightController.helixO1.getPower() * (1f - propO1.damage));

        if (flightController.helixO2 != null)
            flightController.helixO2.setPower(
                flightController.helixO2.getPower() * (1f - propO2.damage));
    }

    // ──────────────────────── Reset ────────────────────────

    /// <summary>
    /// Setzt allen Schaden auf 0 zurueck (fuer Episode-Reset).
    /// </summary>
    public void ResetDamage()
    {
        foreach (var z in allZones)
            z.damage = 0f;
        criticalFired = false;
    }

    // ──────────────────────── Gizmos ────────────────────────

    void OnDrawGizmosSelected()
    {
        // Propeller-Zonen visualisieren
        Transform[] rotors = { rotorV1Transform, rotorV2Transform,
                                rotorO1Transform, rotorO2Transform };
        DamageZone[] zones = { propV1, propV2, propO1, propO2 };
        string[] labels = { "V1", "V2", "O1", "O2" };

        for (int i = 0; i < rotors.Length; i++)
        {
            if (rotors[i] == null) continue;

            // Farbe: gruen = heil, rot = kaputt
            float d = zones[i] != null ? zones[i].damage : 0f;
            Gizmos.color = Color.Lerp(Color.green, Color.red, d);
            Gizmos.DrawWireSphere(rotors[i].position, propellerHitRadius);
        }

        // Body-Bereich
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.3f);
        Gizmos.DrawWireCube(transform.position, new Vector3(0.3f, 0.1f, 0.3f));
    }
}
