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

    public override void Initialize()
    {
        // WICHTIG: MaxStep=0 damit ML-Agents NIEMALS automatisch die Episode beendet
        // MaxStep = 0;

        _drone = GetComponent<SimulatedDroneController>();
        _rb = GetComponent<Rigidbody>();
        _spawnPosition = transform.position;

        Debug.Log($"[DroneAgent] Initialize OK. MaxStep={MaxStep}, SpawnY={_spawnPosition.y:F2}");

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
        int idx = targets.IndexOf(hitObject);
        if (idx < 0) return;

        _targetsCollected++;
        Debug.Log($"[DroneAgent] Target '{hitObject.name}' ({_targetsCollected}/{_totalTargets})");
        AddReward(targetReward);
        hitObject.SetActive(false);

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

    // ═══════════════════════ ML-Agents ═══════════════════════

    public override void OnEpisodeBegin()
    {
        _isEpisodeEnding = false;
        _stepsInEpisode = 0;
        _hasReachedSafeAltitude = false;

        if (_drone != null)
            _drone.ResetController(_spawnPosition, Quaternion.identity);

        RestoreTargets();
        Debug.Log("[DroneAgent] Episode-Reset.");
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        NavdataPacket nav = _drone.Navdata;
        Vector3 av = _rb.angularVelocity;

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
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (_drone == null || _isEpisodeEnding) return;

        _stepsInEpisode++;
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

        // Kleine negative Belohnung pro Step → Agent lernt effizient zu sein
        AddReward(-0.0005f);
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
        if (Input.GetKey(KeyCode.Q)) ca[3] = 1f;
        if (Input.GetKey(KeyCode.E)) ca[3] = -1f;
    }
}
