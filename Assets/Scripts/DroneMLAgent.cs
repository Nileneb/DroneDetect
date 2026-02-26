using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

/// <summary>
/// ML-Agents Agent fuer Drohnen-CSI/Kabel-Detektion.
/// 
/// Zwei Modi:
///   SIM   – Training in Unity mit Rigidbody-Physik + Target-Objekt
///   API   – Inference auf echter Drohne ueber DroneBridgeService
///
/// Observation Space  : 28 floats (VectorSensor) + optionale Kamera-Sensoren
///                       20 Flug/Navigation + 8 WiFi/CSI Features
/// Continuous Actions : 4  (forward, right, up, turn)
/// Discrete Actions   : 1  (0=noop, 1=hover, 2=land)
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class DroneMLAgent : Agent
{
    // ──────────────────────── Mode ────────────────────────
    public enum DroneMode { Sim, Api }

    [Header("Mode")]
    [Tooltip("Sim = lokale Physik (Training), Api = echte Drohne (Inference)")]
    public DroneMode mode = DroneMode.Sim;

    [Header("References – Api Mode")]
    public DroneBridgeService api;

    [Header("References – Sim Mode")]
    [Tooltip("Ziel-Objekt das der Agent finden/anfliegen soll")]
    public Transform target;

    [Tooltip("Boden-Ebene / Terrain (fuer Crash-Erkennung)")]
    public Transform ground;

    [Tooltip("Flight-Controller auf dem Drone-Child (PID + Rotoren)")]
    public droneMovementController droneController;

    [Tooltip("Optional: Waypoint-Tracker fuer Routen-basierte Rewards (readOnly-Modus)")]
    public WaypointProgressTracker waypointTracker;

    [Tooltip("Ultraschall-Hoehensensor (Raycast nach unten, wie beim echten AR.Drone 2.0)")]
    public UltrasonicAltimeter altimeter;

    [Tooltip("Lokalisiertes Schadensmodell (Propeller, Body, Front)")]
    public DroneDamageModel damageModel;

    [Tooltip("Bodenkamera (optional, fuer Visual Observations via CameraSensorComponent)")]
    public DroneBottomCamera bottomCamera;

    [Tooltip("WiFi/CSI-Simulator (liefert 8 Feature-Observations + sendet an Bodenstation)")]
    public WifiSignalSimulator wifi;

    // ──────────────────────── Sim-Physik ────────────────────────
    // Die eigentliche Flugphysik wird jetzt vom droneMovementController
    // gesteuert (PID-Regler + 4 Rotoren mit AddForceAtPosition).
    // Diese Parameter werden nur noch fuer API-Modus / Fallbacks genutzt.
    [Header("Sim Flight (Legacy / API)")]
    public float moveForce = 10f;
    public float turnTorque = 150f;
    public float maxSpeed = 8f;
    public float hoverMultiplier = 1.1f;

    [Header("Debug")]
    [Tooltip("Direkte Keyboard-Steuerung (funktioniert immer, auch ohne Heuristic-Mode)")]
    public bool debugKeyboardControl = false;

    // ──────────────────────── Rewards ────────────────────────
    [Header("Rewards")]
    public float targetReachedReward = 2.0f;
    public float proximityRewardScale = 0.01f;
    public float stayAliveReward = 0.001f;
    public float crashPenalty = -1.0f;
    public float outOfBoundsPenalty = -1.0f;
    public float batteryLowPenalty = -0.5f;

    [Header("Stability Bonus")]
    [Tooltip("Max. Belohnung pro Step wenn Drohne stabil fliegt")]
    public float stabilityReward = 0.002f;
    [Tooltip("Schwelle fuer Aufrichtung (1=perfekt, 0.95=leichte Neigung ok)")]
    [Range(0.8f, 1f)]
    public float uprightThreshold = 0.95f;
    [Tooltip("Max. erlaubte Winkelgeschwindigkeit (rad/s) fuer vollen Bonus")]
    public float maxStableAngularVelocity = 0.5f;

    [Header("Flip Recovery")]
    [Tooltip("Ab diesem Dot-Product (transform.up vs Vector3.up) gilt die Drohne als umgekippt. 0 = seitlich, <0 = kopfueber")]
    public float flippedThreshold = 0.0f;
    [Tooltip("Sekunden die die Drohne hat um sich von einem Ueberschlag zu erholen")]
    public float flipRecoveryTimeSec = 2.0f;
    [Tooltip("Kleine Strafe pro Step waehrend die Drohne umgekippt/am Boden liegt")]
    public float flippedPerStepPenalty = -0.01f;
    [Tooltip("Bonus wenn die Drohne sich aus einem Ueberschlag erfolgreich befreit")]
    public float flipRecoveryReward = 0.5f;
    [Tooltip("Strafe wenn die Drohne sich nicht innerhalb der Gnadenfrist erholt")]
    public float flipFailPenalty = -1.5f;
    [Tooltip("Mindesthoehe ueber Boden ab der eine Erholung als erfolgreich gilt")]
    public float recoveryMinHeight = 0.8f;

    [Header("Damage Rewards")]
    [Tooltip("Strafe pro Schadensereignis (wird mit Schadenshoehe multipliziert)")]
    public float collisionDamagePenaltyScale = -0.5f;
    [Tooltip("Strafe pro Step proportional zum Gesamtschaden")]
    public float ongoingDamagePenaltyScale = -0.001f;
    [Tooltip("Bei kritischem Schaden Episode beenden")]
    public bool endOnCriticalDamage = true;

    [Header("Route Rewards (nur mit WaypointTracker)")]
    [Tooltip("Belohnung pro Meter Fortschritt auf der Route")]
    public float routeProgressReward = 0.05f;
    [Tooltip("Strafe proportional zur Abweichung von der Route (pro Step)")]
    public float routeDeviationPenaltyScale = -0.005f;
    [Tooltip("Ab dieser Abweichung (Meter) Episode abbrechen")]
    public float maxRouteDeviation = 15f;
    [Tooltip("Bonus wenn Flugrichtung mit Routenrichtung uebereinstimmt")]
    public float routeAlignmentRewardScale = 0.002f;

    [Header("Detection / Sequential Targets")]
    [Tooltip("Abstand zum Target ab dem es als 'erfasst' gilt")]
    public float detectionRadius = 2.0f;
    [Tooltip("Wie viele Targets pro Episode eingesammelt werden sollen (0 = unbegrenzt, bis Timeout)")]
    public int maxTargetsPerEpisode = 5;
    [Tooltip("Max. erlaubte Zeit pro Target in Sekunden (fuer Speed-Bonus-Berechnung)")]
    public float maxSecondsPerTarget = 15f;
    [Tooltip("Zusaetzlicher Bonus-Multiplikator fuer schnelles Erreichen")]
    public float targetSpeedBonusScale = 1.0f;
    [Tooltip("Bonus wenn alle Targets einer Episode eingesammelt wurden")]
    public float allTargetsCollectedBonus = 3.0f;

    [Header("Episode")]
    public float maxEpisodeSec = 60f;

    [Header("Spawn")]
    [Tooltip("Bereich fuer zufaellige Target-Platzierung")]
    public float spawnRange = 15f;
    public float spawnHeightMin = 2f;
    public float spawnHeightMax = 10f;
    public float areaHalfExtent = 20f;

    // ──────────────────────── Privat ────────────────────────
    Rigidbody rb;
    float episodeTimer;
    float prevDistToTarget;

    // Flip-Recovery Tracking
    bool isFlipped;            // aktuell im umgekippten Zustand?
    float flippedTimer;        // wie lange schon umgekippt (Sekunden)

    // Sequential Target Tracking
    int targetsCollected;      // Anzahl eingesammelter Targets in dieser Episode
    float timeSinceLastTarget; // Zeit seit letztem Target-Spawn / Episode-Start

    // Damage Tracking
    float pendingDamageReward; // aufgelaufene Schadensstrafe seit letztem Step

    protected override void Awake()
    {
        base.Awake();
        rb = GetComponent<Rigidbody>();

        // Training beschleunigen
        Time.timeScale = 5f;              // 5x schneller trainieren
        Application.targetFrameRate = -1; // kein FPS-Limit
        QualitySettings.vSyncCount = 0;   // VSync aus

        // Damage-Event abonnieren fuer per-Hit Strafen
        if (damageModel != null)
        {
            damageModel.OnDamageReceived += OnDroneHit;
        }
    }

    void OnDestroy()
    {
        if (damageModel != null)
            damageModel.OnDamageReceived -= OnDroneHit;
    }

    /// <summary>Callback vom DroneDamageModel bei jedem Schadensereignis.</summary>
    void OnDroneHit(string zoneName, float damageAmount, float impactSpeed)
    {
        // Strafe proportional zum Schaden aufsammeln (wird im naechsten Step angewendet)
        pendingDamageReward += damageAmount * collisionDamagePenaltyScale;
    }

    // ═══════════════════════ Episode ═══════════════════════
    public override void OnEpisodeBegin()
    {
        episodeTimer = 0f;
        isFlipped = false;
        flippedTimer = 0f;
        targetsCollected = 0;
        timeSinceLastTarget = 0f;
        pendingDamageReward = 0f;

        if (mode == DroneMode.Sim)
        {
            // Drohne zuruecksetzen
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;

            transform.localPosition = new Vector3(0f, spawnHeightMin + 1f, 0f);
            transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

            // Flight-Controller PIDs + Rotoren zuruecksetzen
            if (droneController != null)
                droneController.ResetForEpisode(spawnHeightMin + 1f);

            // Sensoren zuruecksetzen
            if (altimeter != null)
                altimeter.Reset();

            // Schadensmodell zuruecksetzen
            if (damageModel != null)
                damageModel.ResetDamage();

            // WiFi-Simulator zuruecksetzen + Domain Randomization
            if (wifi != null)
            {
                wifi.ResetForEpisode();
                wifi.RandomizeParameters();
            }

            // Waypoint-Tracker zuruecksetzen
            if (waypointTracker != null && waypointTracker.HasWaypoint)
                waypointTracker.ResetProgress();

            // Erstes Target platzieren
            SpawnNewTarget();

            prevDistToTarget = DistToTarget();
        }
        else
        {
            if (api != null) api.Takeoff();
        }
    }

    // ═══════════════════════ Observations (28 floats) ═══════════════════════
    public override void CollectObservations(VectorSensor sensor)
    {
        if (mode == DroneMode.Sim)
        {
            CollectSimObservations(sensor);
        }
        else
        {
            CollectApiObservations(sensor);
        }
    }

    void CollectSimObservations(VectorSensor sensor)
    {
        // --- Eigene Lage (6) ---
        Vector3 localPos = transform.localPosition;
        sensor.AddObservation(localPos.x / areaHalfExtent);          // 0

        // Hoehe: Raycast-Altimeter (realistisch) oder Fallback auf World-Y
        if (altimeter != null && altimeter.HasGround)
            sensor.AddObservation(altimeter.Altitude / spawnHeightMax);   // 1
        else
            sensor.AddObservation(localPos.y / spawnHeightMax);           // 1

        sensor.AddObservation(localPos.z / areaHalfExtent);          // 2

        Vector3 vel = rb.linearVelocity;
        sensor.AddObservation(vel.x / maxSpeed);                      // 3
        sensor.AddObservation(vel.y / maxSpeed);                      // 4
        sensor.AddObservation(vel.z / maxSpeed);                      // 5

        // --- Rotation (normalisiert) (3) ---
        Vector3 rot = transform.localEulerAngles;
        sensor.AddObservation((rot.x > 180f ? rot.x - 360f : rot.x) / 180f); // 6
        sensor.AddObservation((rot.y > 180f ? rot.y - 360f : rot.y) / 180f); // 7
        sensor.AddObservation((rot.z > 180f ? rot.z - 360f : rot.z) / 180f); // 8

        // --- Richtung zum Target (lokal) (3) ---
        if (target != null)
        {
            Vector3 dirWorld = (target.localPosition - localPos);
            Vector3 dirLocal = transform.InverseTransformDirection(dirWorld.normalized);
            sensor.AddObservation(dirLocal.x);                         // 9
            sensor.AddObservation(dirLocal.y);                         // 10
            sensor.AddObservation(dirLocal.z);                         // 11
        }
        else
        {
            sensor.AddObservation(0f); sensor.AddObservation(0f); sensor.AddObservation(0f);
        }

        // --- Distanz zum Target (1) ---
        float dist = DistToTarget();
        sensor.AddObservation(dist / (spawnRange * 2f));               // 12

        // --- Angular velocity (3) ---
        Vector3 angVel = rb.angularVelocity;
        sensor.AddObservation(angVel.x / 5f);                         // 13
        sensor.AddObservation(angVel.y / 5f);                         // 14
        sensor.AddObservation(angVel.z / 5f);                         // 15

        // --- Forward / Up Dot-Products (2) ---
        sensor.AddObservation(Vector3.Dot(transform.forward, Vector3.up));  // 16 – Neigung
        sensor.AddObservation(Vector3.Dot(transform.up, Vector3.up));       // 17 – Aufrichtung

        // --- Route Tracking / Damage / Misc (2) ---
        if (waypointTracker != null && waypointTracker.HasWaypoint)
        {
            // Abweichung von der Route (normalisiert)
            sensor.AddObservation(waypointTracker.DeviationFromRoute / maxRouteDeviation); // 18
            // Richtungs-Alignment: wie gut fliegen wir in Routenrichtung?
            sensor.AddObservation(Vector3.Dot(transform.forward, waypointTracker.RouteDirection)); // 19
        }
        else
        {
            // Gesamtschaden (0..1) – wichtige Info fuer den Agenten
            float dmg = damageModel != null ? damageModel.TotalDamageNormalized : 0f;
            sensor.AddObservation(dmg);                                          // 18
            sensor.AddObservation(episodeTimer / maxEpisodeSec);                 // 19
        }

        // --- WiFi/CSI Features (8) ---                                          // 20-27
        if (wifi != null)
            wifi.AddObservationsToSensor(sensor);
        else
            for (int i = 0; i < 8; i++) sensor.AddObservation(0f);
    }

    void CollectApiObservations(VectorSensor sensor)
    {
        if (api == null)
        {
            // 28 Nullen falls API fehlt
            for (int i = 0; i < 28; i++) sensor.AddObservation(0f);
            return;
        }

        var n = api.nav;
        var q = api.imgQ;

        // Navdata (10)
        sensor.AddObservation(n.altitude / 3000f);
        sensor.AddObservation(n.battery / 100f);
        sensor.AddObservation(n.vx / 1000f);
        sensor.AddObservation(n.vy / 1000f);
        sensor.AddObservation(n.vz / 1000f);
        sensor.AddObservation(n.rotX / 180f);
        sensor.AddObservation(n.rotY / 180f);
        sensor.AddObservation(n.rotZ / 360f);
        sensor.AddObservation(n.state / 7f);
        sensor.AddObservation(n.wifi_signal / 100f);

        // Image Quality (4)
        sensor.AddObservation(q.blur_level / 50f);
        sensor.AddObservation(q.edge_density / 10f);
        sensor.AddObservation(q.compression_ratio);
        sensor.AddObservation(q.noise_level / 50f);

        // Derived (6) – auf 20 auffuellen
        sensor.AddObservation(q.brightness / 100f);
        sensor.AddObservation(episodeTimer / maxEpisodeSec);
        sensor.AddObservation(0f); // placeholder
        sensor.AddObservation(0f);
        sensor.AddObservation(0f);
        sensor.AddObservation(0f);

        // WiFi/CSI Features (8) – auf 28 auffuellen                           // 20-27
        if (wifi != null)
            wifi.AddObservationsToSensor(sensor);
        else
            for (int i = 0; i < 8; i++) sensor.AddObservation(0f);
    }

    // ═══════════════════════ Actions ═══════════════════════
    /// <summary>
    /// Continuous[0-3]: forward, right, up, turn [-1,1]
    /// Discrete[0]: 0=noop, 1=hover, 2=land
    /// </summary>
    public override void OnActionReceived(ActionBuffers actions)
    {
        episodeTimer += Time.fixedDeltaTime;

        float fwd = actions.ContinuousActions[0];
        float rt = actions.ContinuousActions[1];
        float up = actions.ContinuousActions[2];
        float turn = actions.ContinuousActions[3];

        int special = actions.DiscreteActions[0];

        if (mode == DroneMode.Sim)
        {
            SimAction(fwd, rt, up, turn, special);
        }
        else
        {
            ApiAction(fwd, rt, up, turn, special);
        }
    }

    // ─────────── Sim Action (Parrot AR.Drone 2.0 style) ───────────
    /// <summary>
    /// Uebergibt die ML-Agents-Aktionen als AT*PCMD-Befehle an den
    /// droneMovementController, der sie ueber PID-Regler in
    /// realistische Rotor-Kraefte umsetzt.
    /// </summary>
    void SimAction(float fwd, float rt, float up, float turn, int special)
    {
        if (special == 2)
        {
            // Land → Episode beenden
            AddReward(stayAliveReward);
            EndEpisode();
            return;
        }

        // Befehle an Flight-Controller senden (wie AT*PCMD)
        if (droneController != null)
        {
            if (special == 1)
            {
                // Hover: Nullkommando → PIDs stabilisieren die Drohne
                droneController.SetAgentCommand(0f, 0f, 0f, 0f);
            }
            else
            {
                // forward  → pitch,  right → roll,  up → gaz,  turn → yaw
                droneController.SetAgentCommand(fwd, rt, up, turn);
            }
        }

        // ── Rewards ──
        timeSinceLastTarget += Time.fixedDeltaTime;

        // 1) Am-Leben-Bleiben
        AddReward(stayAliveReward);

        // 2) Proximity-Reward (naeher = positiv, weiter = negativ)
        float dist = DistToTarget();
        float delta = prevDistToTarget - dist;   // positiv wenn naeher
        AddReward(delta * proximityRewardScale);
        prevDistToTarget = dist;

        // 2a-i) Aufgelaufene Kollisions-Schadensstrafe anwenden
        if (pendingDamageReward != 0f)
        {
            AddReward(pendingDamageReward);
            pendingDamageReward = 0f;
        }

        // 2a-ii) Laufende Strafe proportional zum Gesamtschaden
        if (damageModel != null && damageModel.TotalDamageNormalized > 0f)
        {
            AddReward(damageModel.TotalDamageNormalized * ongoingDamagePenaltyScale);
        }

        // 2a-iii) Kritischer Schaden → Episode beenden
        if (endOnCriticalDamage && damageModel != null && damageModel.IsCritical)
        {
            AddReward(crashPenalty);
            EndEpisode();
            return;
        }

        // 2a) Stabilitaetsbonus: Belohnung wenn Gyro ruhig + Drohne aufrecht
        {
            float uprightness = Vector3.Dot(transform.up, Vector3.up); // 1 = perfekt aufrecht
            float angVelMag = rb.angularVelocity.magnitude;            // niedrig = ruhig

            // Aufrichtungs-Faktor: 1.0 wenn ueber Schwelle, faellt linear auf 0 ab
            float uprightFactor = Mathf.InverseLerp(uprightThreshold - 0.1f, uprightThreshold, uprightness);

            // Ruhe-Faktor: 1.0 wenn angVel=0, faellt auf 0 bei maxStableAngularVelocity
            float calmFactor = 1f - Mathf.Clamp01(angVelMag / maxStableAngularVelocity);

            // Kombinierter Bonus (beide Faktoren muessen gut sein)
            float bonus = uprightFactor * calmFactor * stabilityReward;
            AddReward(bonus);
        }

        // 2b) Route-basierte Rewards (wenn WaypointTracker aktiv)
        if (waypointTracker != null && waypointTracker.HasWaypoint)
        {
            // Fortschritt auf der Route belohnen
            float progressDelta = waypointTracker.ProgressDelta;
            if (progressDelta > 0f)
                AddReward(progressDelta * routeProgressReward);

            // Abweichung von der Route bestrafen
            float deviation = waypointTracker.DeviationFromRoute;
            AddReward(deviation * routeDeviationPenaltyScale);

            // Alignment-Bonus: Flugrichtung = Routenrichtung
            float alignment = Vector3.Dot(transform.forward, waypointTracker.RouteDirection);
            if (alignment > 0f)
                AddReward(alignment * routeAlignmentRewardScale);

            // Zu weit von der Route abgekommen → Episode Ende
            if (deviation > maxRouteDeviation)
            {
                AddReward(outOfBoundsPenalty);
                EndEpisode();
                return;
            }
        }

        // 3) Target erfasst → Respawn + Speed-Bonus
        if (dist < detectionRadius)
        {
            targetsCollected++;

            // Speed-Bonus: je schneller desto mehr (linear, 0 bei Timeout)
            float timeRatio = Mathf.Clamp01(1f - timeSinceLastTarget / maxSecondsPerTarget);
            float speedBonus = timeRatio * targetSpeedBonusScale;
            AddReward(targetReachedReward + speedBonus);

            // Alle Targets eingesammelt?
            if (maxTargetsPerEpisode > 0 && targetsCollected >= maxTargetsPerEpisode)
            {
                AddReward(allTargetsCollectedBonus);
                EndEpisode();
                return;
            }

            // Naechstes Target spawnen
            SpawnNewTarget();
            timeSinceLastTarget = 0f;
            prevDistToTarget = DistToTarget();
        }

        // 4) Flip-Recovery / Crash-Logik (jetzt mit Raycast-Altimeter)
        float heightAboveGround;
        if (altimeter != null && altimeter.HasGround)
            heightAboveGround = altimeter.AltitudeRaw;
        else
        {
            float groundY = ground != null ? ground.position.y : 0f;
            heightAboveGround = transform.localPosition.y - groundY;
        }
        float uprightnessCrash = Vector3.Dot(transform.up, Vector3.up);

        if (uprightnessCrash < flippedThreshold)
        {
            // Drohne ist umgekippt / kopfueber
            if (!isFlipped)
            {
                // Gerade erst umgekippt → Timer starten
                isFlipped = true;
                flippedTimer = 0f;
            }

            flippedTimer += Time.fixedDeltaTime;

            // Kleine Strafe pro Step (Anreiz: schnell erholen!)
            AddReward(flippedPerStepPenalty);

            // Gnadenfrist abgelaufen → harte Strafe + Episode Ende
            if (flippedTimer >= flipRecoveryTimeSec)
            {
                AddReward(flipFailPenalty);
                EndEpisode();
                return;
            }
        }
        else if (isFlipped)
        {
            // War umgekippt, ist jetzt wieder aufrecht!
            // Recovery gilt nur wenn genuegend Hoehe ueber Boden
            if (heightAboveGround > recoveryMinHeight)
            {
                AddReward(flipRecoveryReward);
            }
            isFlipped = false;
            flippedTimer = 0f;
        }

        // Normaler Crash: zu nah am Boden UND aufrecht (nicht im Flip-Recovery)
        if (heightAboveGround < 0.3f && !isFlipped)
        {
            AddReward(crashPenalty);
            EndEpisode();
            return;
        }

        // 5) Out of Bounds
        Vector3 lp = transform.localPosition;
        if (Mathf.Abs(lp.x) > areaHalfExtent ||
            Mathf.Abs(lp.z) > areaHalfExtent ||
            lp.y > spawnHeightMax * 2f)
        {
            AddReward(outOfBoundsPenalty);
            EndEpisode();
            return;
        }

        // 6) Timeout
        if (episodeTimer >= maxEpisodeSec)
        {
            EndEpisode();
        }
    }

    // ─────────── Api Action (echte Drohne) ───────────
    void ApiAction(float fwd, float rt, float up, float turn, int special)
    {
        if (api == null) return;

        api.Move(fwd, rt, up, turn);

        if (special == 1) api.Hover();
        if (special == 2) { api.Land(); EndEpisode(); return; }

        AddReward(stayAliveReward);

        if (api.nav.battery < 15f)
        {
            AddReward(batteryLowPenalty);
            api.Land(); EndEpisode(); return;
        }

        if (api.status.flying && api.nav.altitude < 50)
        {
            AddReward(crashPenalty);
            EndEpisode(); return;
        }

        api.FetchImageQuality();

        if (episodeTimer >= maxEpisodeSec)
        {
            api.Land(); EndEpisode();
        }
    }

    // ═══════════════════════ Heuristic ═══════════════════════
    /// <summary>Keyboard-Steuerung fuer Demo / Debug</summary>
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        var c = actionsOut.ContinuousActions;
        c[0] = (kb.wKey.isPressed ? 1f : 0f) + (kb.sKey.isPressed ? -1f : 0f);
        c[1] = (kb.dKey.isPressed ? 1f : 0f) + (kb.aKey.isPressed ? -1f : 0f);
        c[2] = kb.spaceKey.isPressed ? 1f : kb.leftShiftKey.isPressed ? -1f : 0f;
        c[3] = kb.qKey.isPressed ? -1f : kb.eKey.isPressed ? 1f : 0f;

        var d = actionsOut.DiscreteActions;
        d[0] = kb.hKey.isPressed ? 1 : kb.lKey.isPressed ? 2 : 0;
    }

    // ═══════════════════════ Debug Keyboard ═══════════════════════
    /// <summary>
    /// Direkte Keyboard-Steuerung die IMMER funktioniert, unabhaengig vom
    /// BehaviorType. Geht jetzt ueber den Flight-Controller (realistische Physik).
    /// WASD = Bewegung, Space/Shift = Hoch/Runter, Q/E = Drehen
    /// </summary>
    void FixedUpdate()
    {
        if (!debugKeyboardControl || mode != DroneMode.Sim) return;
        if (droneController == null) return;

        var kb = Keyboard.current;
        if (kb == null) return;

        float fwd = (kb.wKey.isPressed ? 1f : 0f) + (kb.sKey.isPressed ? -1f : 0f);
        float rt = (kb.dKey.isPressed ? 1f : 0f) + (kb.aKey.isPressed ? -1f : 0f);
        float up = kb.spaceKey.isPressed ? 1f : kb.leftShiftKey.isPressed ? -1f : 0f;
        float turn = (kb.eKey.isPressed ? 1f : 0f) + (kb.qKey.isPressed ? -1f : 0f);

        // Befehle an Flight-Controller (AT*PCMD-style)
        droneController.SetAgentCommand(fwd, rt, up, turn);
    }

    // ═══════════════════════ Helpers ═══════════════════════
    float DistToTarget()
    {
        if (target == null) return 0f;
        return Vector3.Distance(transform.localPosition, target.localPosition);
    }

    /// <summary>
    /// Platziert das Target an einer neuen zufaelligen Position innerhalb
    /// des Spawn-Bereichs. Stellt sicher, dass das Target nicht zu nah
    /// an der aktuellen Drohnenposition spawnt (Mindestabstand: detectionRadius * 2).
    /// </summary>
    void SpawnNewTarget()
    {
        if (target == null) return;

        Vector3 newPos;
        int safety = 0;
        do
        {
            newPos = new Vector3(
                Random.Range(-spawnRange, spawnRange),
                Random.Range(spawnHeightMin, spawnHeightMax),
                Random.Range(-spawnRange, spawnRange)
            );
            safety++;
        }
        while (Vector3.Distance(transform.localPosition, newPos) < detectionRadius * 2f
               && safety < 20);

        target.localPosition = newPos;
    }

    /// <summary>Gizmos: Target-Radius + Bounds fuer Editor</summary>
    void OnDrawGizmosSelected()
    {
        // Detection-Radius um Target
        if (target != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(target.position, detectionRadius);
        }

        // Training-Bounds
        Gizmos.color = new Color(1f, 1f, 0f, 0.3f);
        Vector3 center = transform.parent != null
            ? transform.parent.position + Vector3.up * spawnHeightMax
            : Vector3.up * spawnHeightMax;
        Vector3 size = new Vector3(areaHalfExtent * 2f, spawnHeightMax * 2f, areaHalfExtent * 2f);
        Gizmos.DrawWireCube(center, size);
    }
}
