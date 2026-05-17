using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

/// <summary>
/// Shepherd Phase 4 — baut auf DroneCSI-Flugkompetenz auf.
/// 14 Observations + 4 Continuous Actions: identisch zu DroneAgent
/// damit DroneCSI.onnx als Curriculum-Startpunkt geladen werden kann.
///
/// Phase-4 Reward (User-Spec):
///   - Wolf-Furcht ↑ → kontinuierlicher Reward (Δfear * gain)
///   - Wolf-Panic-Event → einmaliger Bonus
///   - Schaf-Tod → sofortige Strafe
///   - Episode-Ende (5 min): Bonus pro überlebendem Schaf, Multiplikator bei
///     Perfect-Defense (alle Schafe überlebt). Zeitbonus für früheres Ende.
/// </summary>
[RequireComponent(typeof(SimulatedDroneController))]
public class ShepherdDroneAgent : Agent
{
    [Header("References")]
    public DroneScarer scarer;

    [Header("Scarer (rule-based)")]
    [Tooltip("Scarer aktiviert sich automatisch wenn Wolf näher als dieser Wert")]
    public float scarerAutoRadius = 7f;

    [Header("Reward Weights (Phase-4)")]
    [Tooltip("Multiplikator pro Δfear/step (kontinuierlicher Druck).")]
    public float fearDeltaGain = 4f;
    [Tooltip("Einmaliger Bonus, wenn Wolf in Panic verfällt.")]
    public float panicBonus = 3f;
    [Tooltip("Reward pro Frame * ActiveSheep (Schaf-am-Leben-bonus).")]
    public float perSheepAliveBonus = 0.0003f;
    [Tooltip("Strafe pro Step wenn Drohne crash/stalled (kein Hover/Fly).")]
    public float deathPenalty = 1.0f;

    SimulatedDroneController _sdc;
    Rigidbody _rb;
    ShepherdGameManager _gm;
    WolfFear _trackedFear;
    float _lastFear;
    bool _panicLatch;

    public override void Initialize()
    {
        _sdc = GetComponent<SimulatedDroneController>();
        _rb  = GetComponent<Rigidbody>();
        _gm  = FindAnyObjectByType<ShepherdGameManager>();
        SubscribeToGameManager();
    }

    protected override void OnEnable()  { base.OnEnable();  SubscribeToGameManager(); }
    protected override void OnDisable() { base.OnDisable(); UnsubscribeFromGameManager(); }

    void SubscribeToGameManager()
    {
        if (_gm == null) _gm = FindAnyObjectByType<ShepherdGameManager>();
        if (_gm == null) return;
        _gm.OnSheepCaughtEvent += HandleSheepCaught;
        _gm.OnRoundEnded       += HandleRoundEnded;
    }

    void UnsubscribeFromGameManager()
    {
        if (_gm == null) return;
        _gm.OnSheepCaughtEvent -= HandleSheepCaught;
        _gm.OnRoundEnded       -= HandleRoundEnded;
    }

    void HandleSheepCaught(SheepNPC _)
    {
        AddReward(-_gm.sheepCaughtPenalty);
    }

    void HandleRoundEnded(int saved, int caught, float elapsed)
    {
        int initial = _gm.InitialSheepCount;
        if (initial <= 0) initial = 1;

        // End-of-episode bonus: per surviving sheep + multiplier when perfect defense.
        float survivalRatio = (float)saved / initial;
        float endBonus = saved * _gm.sheepSavedBonus;

        if (saved == initial)
            endBonus *= _gm.perfectDefenseMultiplier;

        // Time bonus: rewards finishing early with all sheep alive.
        if (saved == initial && elapsed < _gm.RoundDuration)
            endBonus += (_gm.RoundDuration - elapsed) * 0.05f;

        // Survival-ratio shaping: pushes the agent away from "trade sheep for fear-spam" exploits.
        AddReward(endBonus * Mathf.Lerp(0.2f, 1f, survivalRatio));
        EndEpisode();
    }

    // ── Heuristic: gleiche Tastenbelegung wie DroneAgent ────────────────────
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var ca = actionsOut.ContinuousActions;
        ca[0] =  Input.GetAxis("Vertical");
        ca[1] =  Input.GetAxis("Horizontal");
        ca[2] =  Input.GetKey(KeyCode.Space) ? 1f : Input.GetKey(KeyCode.LeftShift) ? -1f : 0f;
        ca[3] =  Input.GetKey(KeyCode.Q) ? -1f : Input.GetKey(KeyCode.E) ? 1f : 0f;
    }

    // ── Actions: 4 continuous (forward, strafe, up, yaw) ────────────────────
    public override void OnActionReceived(ActionBuffers actions)
    {
        if (_sdc == null) return;
        var ca = actions.ContinuousActions;
        if (_sdc.State == DroneState.Hovering || _sdc.State == DroneState.Flying)
            _sdc.Move(ca[0], -ca[1], ca[2], ca[3]);

        // Rule-based scarer: activate when wolf is close enough
        if (scarer != null && scarer.IsReady)
        {
            var wolf = FindAnyObjectByType<WolfPlayer>();
            if (wolf != null && Vector3.Distance(transform.position, wolf.transform.position) < scarerAutoRadius)
            {
                scarer.Activate();
                _gm?.RecordEvent("scarer_activated");
            }
        }
    }

    // ── 14 observations — same structure as DroneAgent ──────────────────────
    public override void CollectObservations(VectorSensor sensor)
    {
        if (_sdc != null)
        {
            var nav = _sdc.Navdata;
            var av  = _rb != null ? _rb.angularVelocity : Vector3.zero;

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
        else
        {
            for (int i = 0; i < 10; i++) sensor.AddObservation(0f);
        }

        // Primary target: nearest wolf
        var wolfGo = FindAnyObjectByType<WolfPlayer>();
        if (wolfGo != null)
        {
            var dir   = wolfGo.transform.position - transform.position;
            var local = transform.InverseTransformDirection(dir);
            sensor.AddObservation(local.normalized);
            sensor.AddObservation(Mathf.Clamp01(dir.magnitude / 50f));
        }
        else
        {
            sensor.AddObservation(Vector3.zero);
            sensor.AddObservation(0f);
        }
    }

    void FixedUpdate()
    {
        if (_gm == null || !isActiveAndEnabled) return;

        // Per-step survival bonus: rewards keeping sheep alive over time.
        AddReward(perSheepAliveBonus * _gm.ActiveSheep.Count);

        // Fear-based reward — track current wolf's fear delta.
        var wolf = FindAnyObjectByType<WolfPlayer>();
        if (wolf != null)
        {
            if (_trackedFear == null || _trackedFear.gameObject != wolf.gameObject)
                _trackedFear = wolf.GetComponent<WolfFear>();

            if (_trackedFear != null)
            {
                float dFear = _trackedFear.Fear - _lastFear;
                if (dFear > 0f) AddReward(fearDeltaGain * dFear);
                _lastFear = _trackedFear.Fear;

                // Panic latch: only fires once per panic episode.
                if (_trackedFear.IsPanicking && !_panicLatch)
                {
                    AddReward(panicBonus);
                    _panicLatch = true;
                }
                else if (!_trackedFear.IsPanicking)
                {
                    _panicLatch = false;
                }
            }
        }

        // Crash detection: terminate early with penalty if drone hits emergency state.
        if (_sdc != null && _sdc.State == DroneState.Emergency)
        {
            AddReward(-deathPenalty);
            EndEpisode();
        }
    }
}
