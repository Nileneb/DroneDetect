using UnityEngine;

/// <summary>
/// Scene adapter of <see cref="BehaviourMachine"/>: collects drone, perception and operator input, ticks the
/// machine and is the only component that sends flight commands to the <see cref="IDroneController"/>.
/// The policy (DroneFollowAgent) only offers its command via <see cref="OfferPolicy"/>.
/// Works with the simulator and with the real AR.Drone; shadow mode (real drone) hovers instead of moving.
/// </summary>
public sealed class DroneBehaviour : MonoBehaviour
{
    public BehaviourSettings settings = new BehaviourSettings();
    public DroneFollowSentisPerception perception;
    [Tooltip("Simulator: treat the target as lost when the detector does not see it (same as on the phone).")]
    public bool logTransitions = true;

    IDroneController _controller;
    ArDroneFollowController _ardrone;
    BehaviourMachine _machine;
    OperatorCommand _pendingOperator;
    Vector4 _policy;
    bool _appPaused;
    BehaviourOutput _last;

    public BehaviourState State => _machine?.State ?? BehaviourState.Idle;
    public string Status => _machine == null ? "Idle" : _last.Status ?? "Idle";
    public string LastReason => _machine?.LastReason ?? "";
    public float BatteryFiltered => _last.BatteryFiltered;
    public bool ShadowActive => _ardrone != null && _ardrone.shadowMode;
    public BehaviourSettings Settings => settings;

    void OnEnable()
    {
        foreach (MonoBehaviour component in GetComponents<MonoBehaviour>())
            if (component is IDroneController controller)
            {
                _controller = controller;
                break;
            }
        _ardrone = _controller as ArDroneFollowController;
        if (perception == null)
            perception = GetComponent<DroneFollowSentisPerception>();
        if (_controller == null)
            Debug.LogError("[Behaviour] No IDroneController on this object.", this);

        settings.maxAltitude = _ardrone != null ? _ardrone.maxAltitudeMeters : settings.maxAltitude;
        if (_ardrone != null)
            settings.landBelowBatteryPercent = _ardrone.landBelowBatteryPercent;
        _machine = new BehaviourMachine(settings);
    }

    public void Request(OperatorCommand command) => _pendingOperator = command;

    /// <summary>The policy's latest command (forward, left, up, turn); only Follow forwards it to the drone.</summary>
    public void OfferPolicy(Vector4 command)
    {
        _policy = command;
        _ardrone?.ReportPolicy(command);
    }

    /// <summary>The app really left the foreground; lands via Failsafe on the next tick.</summary>
    public void NotifyAppPaused() => _appPaused = true;

    void Update()
    {
        if (_machine == null || _controller == null)
            return;

        NavdataPacket nav = _controller.Navdata;
        var input = new BehaviourInputs
        {
            DroneState = _controller.State,
            Altitude = nav != null ? nav.altitude : 0f,
            Battery = nav != null ? nav.battery : 0f,
            LinkUp = _ardrone == null || _ardrone.IsLinkUp,
            TargetVisible = perception != null && perception.Current.Visible,
            Policy = _policy,
            Operator = _pendingOperator,
            AppPaused = _appPaused
        };
        _pendingOperator = OperatorCommand.None;
        _appPaused = false;

        BehaviourState before = _machine.State;
        _last = _machine.Tick(input, Time.deltaTime);
        if (logTransitions && _last.TransitionReason != null)
            Debug.Log($"[Behaviour] t={Time.time:F1}s {before} -> {_last.State} ({_last.TransitionReason}) alt={input.Altitude:F2}m battery={input.Battery:F0}% filtered={_last.BatteryFiltered:F0}% target={input.TargetVisible} shadow={ShadowActive}");

        if (_last.RequestLand)
            _controller.Land();
        else if (_last.RequestTakeoff)
            _controller.Takeoff();
        else if (_last.RequestMove && !ShadowActive)
            _controller.Move(_last.Move.x, _last.Move.y, _last.Move.z, _last.Move.w);
        else if (_last.State != BehaviourState.Takeoff && _last.State != BehaviourState.Idle)
            _controller.Hover();
    }
}
