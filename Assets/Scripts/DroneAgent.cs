using System.Collections.Generic;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

/// <summary>
/// DroneAgent — ML-Agent fuer den AR-Drone 2.0 Digital Twin.
///
/// Keyboard-Steuerung:
///   T = Takeoff    G = Land    R = Emergency Reset
///   W/S = vor/zurueck    A/D = links/rechts
///   Space/LShift = hoch/runter    Q/E = drehen
///
/// Parcour-System:
///   - Targets im Inspektor als Liste zuweisen
///   - Beruehrung eines Targets → Belohnung, Target wird deaktiviert
///   - Alle Targets eingesammelt → Bonus + Reset
///   - Crash-Detection optional (Checkbox im Inspektor)
/// </summary>
[RequireComponent(typeof(SimulatedDroneController))]
public class DroneAgent : Agent
{
    SimulatedDroneController _drone;
    Rigidbody _rb;

    // ═══════════════════════ Parcour-System ═══════════════════════

    [Header("Parcour Targets")]
    [Tooltip("Liste der Targets die abgeflogen werden sollen.")]
    public List<GameObject> targets = new List<GameObject>();

    [Header("Rewards")]
    public float targetReward = 1.0f;
    public float allTargetsBonus = 2.0f;
    public float groundPenalty = -1.0f;
    public float wallPenalty = -1.5f;

    [Header("Crash Detection")]
    [Tooltip("Crash-Detection aktivieren? Fuer manuelles Testen AUSSCHALTEN!")]
    public bool enableCrashDetection = false;

    [Header("Episode Control")]
    [Tooltip("Maximale Agent-Steps pro Episode (0 = aus).")]
    public int episodeStepLimit = 1500;

    [Tooltip("Kleine Timeout-Strafe, wenn Episode per Step-Limit endet.")]
    public float timeoutPenalty = -0.2f;

    [Tooltip("Tag fuer Boden-Objekte (wird nur bestraft wenn enableCrashDetection=true)")]
    public string groundTag = "Ground";

    [Tooltip("Tag fuer Wand-Objekte (wird immer bestraft)")]
    public string wallTag = "Wall";

    [Tooltip("Hoehe ab der die Drohne als sicher gilt (m ueber Spawn)")]
    public float safeAltitude = 0.5f;

    [Tooltip("Hoehe unter der ein Absturz erkannt wird (m ueber Spawn)")]
    public float crashAltitude = 0.15f;

    // ═══════════════════════ Public Properties (HUD) ═══════════════════════

    public DroneState CurrentDroneState => _drone?.State ?? DroneState.Landed;
    public float Altitude => _drone?.Navdata.altitude ?? 0f;
    public float Speed
    {
        get
        {
            if (_drone == null) return 0f;
            var n = _drone.Navdata;
            return Mathf.Sqrt(n.vx * n.vx + n.vy * n.vy + n.vz * n.vz);
        }
    }
    public float Battery => _drone?.Navdata.battery ?? 0f;
    public int TargetsCollected => _targetsCollected;
    public int TotalTargets => _totalTargets;
    public bool AllTargetsCollected => _totalTargets > 0 && _targetsCollected >= _totalTargets;
    public int StepsInEpisode => _stepsInEpisode;
    public int CurrentEpisodeStepLimit => episodeStepLimit;

    // Interne Variablen
    List<Vector3> _targetStartPositions = new List<Vector3>();
    List<Quaternion> _targetStartRotations = new List<Quaternion>();
    List<Vector3> _targetStartScales = new List<Vector3>();
    int _targetsCollected;
    int _totalTargets;
    bool _hasReachedSafeAltitude;
    Vector3 _spawnPosition;
    int _stepsInEpisode;
    bool _isEpisodeEnding;
    int _defaultEpisodeStepLimit;
    int _currentTargetIndex;
    float _previousDistance;

    [Header("Hindernisse")]
    [Tooltip("Hindernisse die ab Level 3 aktiviert werden.")]
    public List<GameObject> obstacles = new List<GameObject>();

    [Header("Curriculum")]
    [Tooltip("-1 = automatisch (vom Trainer). 0-4 = manueller Override (z.B. fuer Demo-Aufnahme).")]
    public int overrideCurriculumLevel = -1;

    [Header("Parcours Varianten")]
    public List<ParcourLayout> parcourLayouts = new List<ParcourLayout>();

    [Header("Depth Camera")]
    [Tooltip("Optionale DroneDepthCamera-Komponente (child GameObject).")]
    public DroneDepthCamera depthCamera;

    [Header("Target-Jitter")]
    [Tooltip("Zufaelliger Positions-Offset pro Target bei jedem Episode-Reset.")]
    public Vector3 targetJitterBounds = new Vector3(1.5f, 0.5f, 1.5f);

    public override void Initialize()
    {
        MaxStep = 5000;
        _defaultEpisodeStepLimit = episodeStepLimit;  // Speichern des default Step-Limits

        _drone = GetComponent<SimulatedDroneController>();
        _rb = GetComponent<Rigidbody>();
        _spawnPosition = transform.position;

        Debug.Log($"[DroneAgent] Initialize OK. MaxStep={MaxStep}, episodeStepLimit={episodeStepLimit}, SpawnY={_spawnPosition.y:F2}");

        DroneHUD.CreateHUD(this);

        // Originalpositionen der Targets sichern
        _totalTargets = targets.Count;
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] != null)
            {
                _targetStartPositions.Add(targets[i].transform.position);
                _targetStartRotations.Add(targets[i].transform.rotation);
                _targetStartScales.Add(targets[i].transform.localScale);
            }
            else
            {
                _targetStartPositions.Add(Vector3.zero);
                _targetStartRotations.Add(Quaternion.identity);
                _targetStartScales.Add(Vector3.one);
            }
        }
    }

    void Update()
    {
        if (_drone == null)
        {
            Debug.LogError("[DroneAgent] _drone ist NULL!");
            return;
        }

        // ── Optionale Crash-Detection (hoehen-basiert, KEIN Collision) ──
        if (enableCrashDetection)
        {
            float alt = transform.position.y - _spawnPosition.y;
            if (alt >= safeAltitude)
                _hasReachedSafeAltitude = true;

            // Crash NUR wenn: fliegt aktiv + war hoch genug + jetzt zu niedrig
            if (_hasReachedSafeAltitude
                && _drone.State == DroneState.Flying
                && alt < crashAltitude)
            {
                ApplyCrashPenalty(groundPenalty, $"ALT crash: alt={alt:F2}m");
            }
        }

        // ── Keyboard-Steuerung ──
        if (Input.GetKeyDown(KeyCode.T))
        {
            Debug.Log("[DroneAgent] T → Takeoff");
            _drone.Takeoff();
        }
        if (Input.GetKeyDown(KeyCode.G))
        {
            Debug.Log("[DroneAgent] G → Land");
            _drone.Land();
        }
        if (Input.GetKeyDown(KeyCode.R))
        {
            Debug.Log("[DroneAgent] R → EmergencyReset");
            _drone.EmergencyReset();
        }

        // Flugsteuerung nur wenn in der Luft
        if (_drone.State != DroneState.Hovering && _drone.State != DroneState.Flying)
            return;

        float fwd = 0f, left = 0f, up = 0f, turn = 0f;

        if (Input.GetKey(KeyCode.W)) fwd = 1f;
        if (Input.GetKey(KeyCode.S)) fwd = -1f;
        if (Input.GetKey(KeyCode.A)) left = 1f;
        if (Input.GetKey(KeyCode.D)) left = -1f;
        if (Input.GetKey(KeyCode.Space)) up = 1f;
        if (Input.GetKey(KeyCode.LeftShift)) up = -1f;
        if (Input.GetKey(KeyCode.Q)) turn = 1f;
        if (Input.GetKey(KeyCode.E)) turn = -1f;

        if (fwd == 0f && left == 0f && up == 0f && turn == 0f)
            _drone.Hover();
        else
            _drone.Move(fwd, left, up, turn);
    }

    // ═══════════════════════ Target-Kollision ═══════════════════════

    void OnCollisionEnter(Collision collision)
    {
        HandleContact(collision.gameObject);
    }

    void OnTriggerEnter(Collider other)
    {
        HandleContact(other.gameObject);
    }

    void HandleContact(GameObject hitObject)
    {
        if (hitObject == null)
            return;

        // WICHTIG: Wandkontakt immer bestrafen, unabhaengig von enableCrashDetection.
        if (!string.IsNullOrEmpty(wallTag) && hitObject.CompareTag(wallTag))
        {
            ApplyCrashPenalty(wallPenalty, $"WALL contact: {hitObject.name}");
            return;
        }

        // Bodenkontakt nur bestrafen, wenn Crash-Detection aktiv ist.
        if (enableCrashDetection && !string.IsNullOrEmpty(groundTag) && hitObject.CompareTag(groundTag))
        {
            ApplyCrashPenalty(groundPenalty, $"GROUND contact: {hitObject.name}");
            return;
        }

        CheckTargetHit(hitObject);
    }

    void ApplyCrashPenalty(float penalty, string reason)
    {
        if (_isEpisodeEnding)
            return;

        Debug.LogWarning($"[DroneAgent] CRASH! {reason}");
        AddReward(penalty);
        EndCurrentEpisode(reason);
    }

    void EndCurrentEpisode(string reason)
    {
        if (_isEpisodeEnding)
            return;

        _isEpisodeEnding = true;
        Debug.Log($"[DroneAgent] EndEpisode: {reason}");
        EndEpisode();
    }

    void CheckTargetHit(GameObject hitObject)
    {
        if (hitObject == null || targets == null || targets.Count == 0)
            return;

        // Nur das aktuell geforderte Target akzeptieren.
        GameObject currentTarget = GetNextActiveTarget();
        if (currentTarget == null || hitObject != currentTarget)
            return;

        _targetsCollected++;

        // Zeitfaktor: schneller eingesammelt = mehr Reward (bis 2x)
        float timeFactor = 1f + (float)(episodeStepLimit - _stepsInEpisode) / episodeStepLimit;

        // Multiplikator-Reward: 1.,2.,3.,4. Target wird zunehmend belohnt + Zeitbonus.
        float scaledReward = targetReward * _targetsCollected * timeFactor;
        AddReward(scaledReward);

        // Bonus-Steps pro erreichtem Target.
        episodeStepLimit += 500;
        MaxStep = episodeStepLimit;

        Debug.Log($"[DroneAgent] Target '{hitObject.name}' ({_targetsCollected}/{_totalTargets}) " +
                  $"Reward: {scaledReward:F1} | episodeStepLimit: {episodeStepLimit}");

        hitObject.SetActive(false);
        _currentTargetIndex++;
        UpdateTargetVisuals();

        // Distanz zum neuen Target initialisieren (damit kein falscher Delta-Reward)
        _previousDistance = GetDistanceToCurrentTarget();

        if (_targetsCollected >= _totalTargets && _totalTargets > 0)
        {
            Debug.Log("[DroneAgent] Alle Targets! Bonus + Episode-Ende");
            AddReward(allTargetsBonus);
            EndCurrentEpisode("all targets collected");
        }
    }


    // ═══════════════════════ Target-Management ═══════════════════════

    void RestoreTargets()
    {
        _targetsCollected = 0;
        _currentTargetIndex = 0;
        _hasReachedSafeAltitude = false;
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] != null)
            {
                targets[i].SetActive(true);
                targets[i].transform.position = _targetStartPositions[i];
                targets[i].transform.rotation = _targetStartRotations[i];
                targets[i].transform.localScale = _targetStartScales[i];
            }
        }
    }

    void UpdateTargetVisuals()
    {
        // Nur aktuelles Target sichtbar, Rest ausgeblendet.
        // Targets jenseits _totalTargets bleiben immer aus (Curriculum).
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] == null)
                continue;

            targets[i].SetActive(i == _currentTargetIndex && i < _totalTargets);
        }
    }

    // ═══════════════════════ Curriculum ═══════════════════════

    void ConfigureParcours(int level)
    {
        _totalTargets = GetTargetCountForLevel(level);
        _currentTargetIndex = 0;
        _targetsCollected = 0;

        // Zufällige Route aus verfügbaren Layouts wählen (falls vorhanden)
        if (parcourLayouts.Count > 0)
        {
            int layoutIndex = Random.Range(0, parcourLayouts.Count);
            ParcourLayout layout = parcourLayouts[layoutIndex];

            for (int i = 0; i < targets.Count && i < layout.targetPositions.Count; i++)
            {
                if (targets[i] != null && layout.targetPositions[i] != null)
                    targets[i].transform.position = layout.targetPositions[i].position;
            }

            Debug.Log($"[DroneAgent] Parcours: {layout.name}, Level: {level}");
        }

        // Jitter: zufaelliger Positions-Offset auf Basis-Positionen anwenden
        if (targetJitterBounds.sqrMagnitude > 0f)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] == null) continue;
                Vector3 jitter = new Vector3(
                    Random.Range(-targetJitterBounds.x, targetJitterBounds.x),
                    Random.Range(-targetJitterBounds.y, targetJitterBounds.y),
                    Random.Range(-targetJitterBounds.z, targetJitterBounds.z));
                targets[i].transform.position = _targetStartPositions[i] + jitter;
            }
        }

        EnableObstacles(level);
        UpdateTargetVisuals();
    }

    int GetTargetCountForLevel(int level)
    {
        switch (level)
        {
            case 0: return Mathf.Min(1, targets.Count);
            case 1: return Mathf.Min(2, targets.Count);
            case 2: return Mathf.Min(3, targets.Count);
            default: return targets.Count;
        }
    }

    void EnableObstacles(int level)
    {
        bool active = level >= 3;
        bool moving = level >= 4;
        for (int i = 0; i < obstacles.Count; i++)
        {
            if (obstacles[i] == null) continue;
            obstacles[i].SetActive(active);
            var mo = obstacles[i].GetComponent<MovingObstacle>();
            if (mo != null) mo.enabled = moving;
        }
    }

    // ═══════════════════════ ML-Agents ═══════════════════════

    public override void OnEpisodeBegin()
    {
        _isEpisodeEnding = false;
        _stepsInEpisode = 0;
        _hasReachedSafeAltitude = false;
        
        // Step-Limit auf Basis-Wert zurücksetzen
        episodeStepLimit = _defaultEpisodeStepLimit;
        MaxStep = episodeStepLimit;  // Beide in Sync halten

        if (_drone != null)
            _drone.ResetController(_spawnPosition, Quaternion.identity);

        RestoreTargets();

        // Curriculum-Level bestimmen: Override oder vom Trainer
        int level;
        if (overrideCurriculumLevel >= 0)
        {
            level = overrideCurriculumLevel;
        }
        else
        {
            float difficulty = Academy.Instance.EnvironmentParameters
                                .GetWithDefault("parcours_difficulty", 0f);
            level = (int)difficulty;
        }
        ConfigureParcours(level);

        // Initiale Distanz zum ersten Target setzen
        _previousDistance = GetDistanceToCurrentTarget();

        Debug.Log($"[DroneAgent] Episode-Reset. Level={level}, Targets={_totalTargets}, episodeStepLimit={episodeStepLimit}, MaxStep={MaxStep}");
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        NavdataPacket nav = _drone.Navdata;
        Vector3 av = _rb.angularVelocity;

        // Eigenzustand (10 Werte)
        sensor.AddObservation(nav.altitude / 10f);
        sensor.AddObservation(nav.vx / 5f);
        sensor.AddObservation(nav.vy / 5f);
        sensor.AddObservation(nav.vz / 1f);
        sensor.AddObservation(nav.rotX / 180f);
        sensor.AddObservation(nav.rotY / 180f);
        sensor.AddObservation(nav.rotZ / 360f);
        sensor.AddObservation(av.x / 5f);
        sensor.AddObservation(av.y / 5f);
        sensor.AddObservation(av.z / 5f);

        // Richtung + Distanz zum naechsten aktiven Target (4 Werte)
        GameObject nextTarget = GetNextActiveTarget();
        if (nextTarget != null)
        {
            Vector3 dirWorld = nextTarget.transform.position - transform.position;
            Vector3 dirLocal = transform.InverseTransformDirection(dirWorld);
            float dist = dirWorld.magnitude;
            sensor.AddObservation(dirLocal.normalized);  // 3 Werte
            sensor.AddObservation(dist / 50f);            // 1 Wert (normalisiert)
        }
        else
        {
            sensor.AddObservation(Vector3.zero);  // 3
            sensor.AddObservation(0f);            // 1
        }
    }

    GameObject GetNextActiveTarget()
    {
        // Sequentiell: immer erstes noch aktives Target ab aktuellem Index.
        for (int i = Mathf.Max(_currentTargetIndex, 0); i < targets.Count; i++)
        {
            if (targets[i] != null && targets[i].activeSelf)
            {
                _currentTargetIndex = i;
                return targets[i];
            }
        }

        return null;
    }

    float GetDistanceToCurrentTarget()
    {
        GameObject t = GetNextActiveTarget();
        if (t == null) return 0f;
        Vector3 d = t.transform.position - transform.position;
        return Mathf.Sqrt(d.x * d.x + d.z * d.z + d.y * d.y * 4f);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (_drone == null || _isEpisodeEnding) return;

        _stepsInEpisode++;
        depthCamera?.NotifyStep();
        if (episodeStepLimit > 0 && _stepsInEpisode >= episodeStepLimit)
        {
            AddReward(timeoutPenalty);
            EndCurrentEpisode($"step limit reached ({episodeStepLimit})");
            return;
        }

        // Continuous Actions: 0=fwd, 1=left, 2=up, 3=turn, 4=takeoff/land
        var ca = actions.ContinuousActions;

        float fwd = Mathf.Clamp(ca[0], -1f, 1f);
        float left = Mathf.Clamp(ca[1], -1f, 1f);
        float up = Mathf.Clamp(ca[2], -1f, 1f);
        float turn = Mathf.Clamp(ca[3], -1f, 1f);

        // Auto-Takeoff: wenn gelandet, automatisch starten
        if (_drone.State == DroneState.Landed)
        {
            _drone.Takeoff();
            return;
        }

        // Nur steuern wenn in der Luft
        if (_drone.State == DroneState.Hovering || _drone.State == DroneState.Flying)
        {
            if (Mathf.Abs(fwd) < 0.05f && Mathf.Abs(left) < 0.05f
                && Mathf.Abs(up) < 0.05f && Mathf.Abs(turn) < 0.05f)
                _drone.Hover();
            else
                _drone.Move(fwd, left, up, turn);
        }

        // ═══════════════════ DISTANZ-GRADIENT (Delta-basiert) ═══════════════════
        GameObject next = GetNextActiveTarget();
        if (next != null)
        {
            Vector3 delta = next.transform.position - transform.position;
            float weightedDist = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z + delta.y * delta.y * 4f);
            
            // Delta-Reward: naeher kommen = positiv, weiter weg = negativ
            // Kein Akkumulationsproblem mehr!
            float distanceDelta = _previousDistance - weightedDist;
            AddReward(distanceDelta * 0.5f);
            _previousDistance = weightedDist;
            
            // Proximity-Bonus unter 3m: positiver Reward zieht den Agent rein
            if (weightedDist < 3f)
            {
                float proximityBonus = (3f - weightedDist) * 0.01f;  // +0.03 bei 0m
                AddReward(proximityBonus);
            }
        }

        // Step-Penalty (minimal — Agent soll nicht gehetzt werden)
        //AddReward(-0.00001f);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var ca = actionsOut.ContinuousActions;
        ca[0] = 0f; // fwd
        ca[1] = 0f; // left
        ca[2] = 0f; // up
        ca[3] = 0f; // turn

        if (Input.GetKey(KeyCode.W)) ca[0] = 1f;
        if (Input.GetKey(KeyCode.S)) ca[0] = -1f;
        if (Input.GetKey(KeyCode.A)) ca[1] = 1f;
        if (Input.GetKey(KeyCode.D)) ca[1] = -1f;
        if (Input.GetKey(KeyCode.Space)) ca[2] = 1f;
        if (Input.GetKey(KeyCode.LeftShift)) ca[2] = -1f;
        if (Input.GetKey(KeyCode.E)) ca[3] = 1f;
        if (Input.GetKey(KeyCode.Q)) ca[3] = -1f;
    }
}

[System.Serializable]
public class ParcourLayout
{
    public string name;
    public List<Transform> targetPositions;
}
