using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Mission state machine for the follow drone. Pure logic (no scene access, time comes in as dt) so the same
/// code drives the simulator and the real AR.Drone and is covered by EditMode tests. The policy is only
/// forwarded in Follow; every other state decides on its own.
/// </summary>
public sealed class BehaviourMachine
{
    readonly BehaviourSettings _s;
    readonly Queue<(float time, float value)> _batterySamples = new Queue<(float, float)>();
    float _clock;
    float _stateTime;
    float _lostTime;
    float _visibleTime;
    float _linkLostTime;
    float _batteryLowTime;
    float _searchPhaseTime;
    bool _searchClimbing;
    bool _autoSearch;
    bool _enteredThisTick;
    string _reason;

    public BehaviourState State { get; private set; } = BehaviourState.Idle;
    public string LastReason { get; private set; } = "";

    public BehaviourMachine(BehaviourSettings settings = null)
    {
        _s = settings ?? new BehaviourSettings();
    }

    static bool Airborne(DroneState state) =>
        state == DroneState.Hovering || state == DroneState.Flying || state == DroneState.TakingOff;

    public BehaviourOutput Tick(in BehaviourInputs input, float dt)
    {
        _clock += dt;
        _stateTime += dt;
        _reason = null;
        _enteredThisTick = false;

        float batteryFiltered = UpdateBattery(input, dt);
        _visibleTime = input.TargetVisible ? _visibleTime + dt : 0f;
        // WHY(false detections): one stray detection must not restart the lost-target clock (twin: a flickering
        // false match kept Follow alive for 90 s without a target); only a stable sighting resets it.
        if (!input.TargetVisible)
            _lostTime += dt;
        else if (_visibleTime >= _s.lostResetSeconds)
            _lostTime = 0f;
        _linkLostTime = input.LinkUp ? 0f : _linkLostTime + dt;

        bool flightPhase = State != BehaviourState.Idle && State != BehaviourState.Landing && State != BehaviourState.Failsafe;
        if (State == BehaviourState.Idle)
            TickIdle(input);
        else
        {
            if (input.DroneState == DroneState.Emergency)
                Enter(BehaviourState.Idle, "drone emergency (motors off)");
            else if (flightPhase && CheckFailsafe(input, batteryFiltered))
            {
            }
            else if (flightPhase && input.Operator == OperatorCommand.Land)
                Enter(BehaviourState.Landing, "operator: land");
            else
                TickState(input, dt);
        }

        return BuildOutput(input, batteryFiltered);
    }

    void TickIdle(in BehaviourInputs input)
    {
        if (input.Operator != OperatorCommand.Takeoff)
            return;
        if (!input.LinkUp)
            LastReason = "takeoff refused: no link";
        else if (input.DroneState != DroneState.Landed)
            LastReason = "takeoff refused: drone not landed";
        else
            Enter(BehaviourState.Takeoff, "operator: takeoff");
    }

    bool CheckFailsafe(in BehaviourInputs input, float batteryFiltered)
    {
        if (input.AppPaused)
        {
            Enter(BehaviourState.Failsafe, "app paused");
            return true;
        }
        if (Airborne(input.DroneState) && _linkLostTime > _s.linkLostSeconds)
        {
            Enter(BehaviourState.Failsafe, "link lost");
            return true;
        }
        if (Airborne(input.DroneState) && batteryFiltered > 0f && batteryFiltered < _s.landBelowBatteryPercent)
        {
            if (_batteryLowTime >= _s.batteryDebounceSeconds)
            {
                Enter(BehaviourState.Failsafe, $"battery {batteryFiltered:F0}%");
                return true;
            }
        }
        return false;
    }

    void TickState(in BehaviourInputs input, float dt)
    {
        switch (State)
        {
            case BehaviourState.Takeoff:
                if (input.DroneState == DroneState.Hovering || input.DroneState == DroneState.Flying)
                    Enter(BehaviourState.Hover, "airborne");
                else if (_stateTime > _s.takeoffTimeoutSeconds)
                    Enter(BehaviourState.Failsafe, "takeoff timeout");
                break;

            case BehaviourState.Hover:
                if (input.Operator == OperatorCommand.Search)
                    EnterSearch(auto: false, "operator: search");
                else if (input.Operator == OperatorCommand.Follow)
                {
                    if (input.TargetVisible)
                        Enter(BehaviourState.Follow, "operator: follow");
                    else
                        EnterSearch(auto: false, "operator: follow, no target yet");
                }
                break;

            case BehaviourState.Search:
                TickSearch(input, dt);
                break;

            case BehaviourState.Follow:
                if (input.Operator == OperatorCommand.Hover)
                    Enter(BehaviourState.Hover, "operator: hover");
                else if (_lostTime > _s.followLostSeconds)
                    Enter(BehaviourState.LostTarget, "target lost");
                break;

            case BehaviourState.LostTarget:
                if (input.Operator == OperatorCommand.Hover)
                    Enter(BehaviourState.Hover, "operator: hover");
                else if (_visibleTime >= _s.acquireSeconds)
                    Enter(BehaviourState.Follow, "target reacquired");
                else if (_lostTime > _s.lostToLandSeconds)
                    Enter(BehaviourState.Landing, "target lost too long");
                else if (_lostTime > _s.lostToSearchSeconds)
                    EnterSearch(auto: true, "target lost: searching");
                break;

            case BehaviourState.Landing:
            case BehaviourState.Failsafe:
                if (input.DroneState == DroneState.Landed)
                    Enter(BehaviourState.Idle, "landed");
                break;
        }
    }

    void TickSearch(in BehaviourInputs input, float dt)
    {
        if (input.Operator == OperatorCommand.Hover)
        {
            Enter(BehaviourState.Hover, "operator: hover");
            return;
        }
        if (_visibleTime >= _s.acquireSeconds)
        {
            Enter(BehaviourState.Follow, "target found");
            return;
        }
        if (_stateTime > _s.searchTimeoutSeconds)
        {
            if (_autoSearch)
                Enter(BehaviourState.Landing, "search timeout");
            else
                Enter(BehaviourState.Hover, "search timeout");
            return;
        }

        _searchPhaseTime += dt;
        if (_searchClimbing)
        {
            if (_searchPhaseTime >= _s.searchClimbSeconds)
            {
                _searchClimbing = false;
                _searchPhaseTime = 0f;
            }
        }
        else if (_searchPhaseTime >= _s.SearchRevolutionSeconds)
        {
            _searchPhaseTime = 0f;
            _searchClimbing = input.Altitude < Mathf.Min(_s.searchMaxAltitude, _s.maxAltitude) - 0.1f;
        }
    }

    void EnterSearch(bool auto, string reason)
    {
        _autoSearch = auto;
        _searchPhaseTime = 0f;
        _searchClimbing = false;
        Enter(BehaviourState.Search, reason);
    }

    void Enter(BehaviourState state, string reason)
    {
        State = state;
        _stateTime = 0f;
        _enteredThisTick = true;
        _reason = reason;
        LastReason = reason;
        if (state == BehaviourState.Idle || state == BehaviourState.Hover)
            _autoSearch = false;
        // Following starts with a clean clock: time without target before it must not count against it.
        if (state == BehaviourState.Follow)
            _lostTime = 0f;
    }

    float UpdateBattery(in BehaviourInputs input, float dt)
    {
        if (input.Battery > 0f)
            _batterySamples.Enqueue((_clock, input.Battery));
        while (_batterySamples.Count > 0 && _clock - _batterySamples.Peek().time > _s.batteryWindowSeconds)
            _batterySamples.Dequeue();

        // Windowed maximum: a load sag is short, a really empty battery stays low.
        float filtered = 0f;
        foreach (var sample in _batterySamples)
            if (sample.value > filtered)
                filtered = sample.value;

        bool low = filtered > 0f && filtered < _s.landBelowBatteryPercent;
        _batteryLowTime = low ? _batteryLowTime + dt : 0f;
        return filtered;
    }

    BehaviourOutput BuildOutput(in BehaviourInputs input, float batteryFiltered)
    {
        var output = new BehaviourOutput
        {
            State = State,
            TransitionReason = _reason,
            BatteryFiltered = batteryFiltered,
            RequestTakeoff = _enteredThisTick && State == BehaviourState.Takeoff,
            RequestLand = State == BehaviourState.Landing || State == BehaviourState.Failsafe,
            Status = StatusText(input)
        };

        switch (State)
        {
            case BehaviourState.Follow:
                output.RequestMove = true;
                output.Move = input.Policy;
                if (input.Altitude >= _s.maxAltitude)
                    output.Move.z = Mathf.Min(output.Move.z, 0f);
                break;
            case BehaviourState.Search:
                output.RequestMove = true;
                output.Move = _searchClimbing
                    ? new Vector4(0f, 0f, _s.searchClimb, 0f)
                    : new Vector4(0f, 0f, 0f, _s.searchYaw);
                break;
        }
        return output;
    }

    string StatusText(in BehaviourInputs input)
    {
        string suffix = string.IsNullOrEmpty(LastReason) ? "" : $" ({LastReason})";
        switch (State)
        {
            case BehaviourState.Idle: return "Idle" + suffix;
            case BehaviourState.Takeoff: return "Taking off";
            case BehaviourState.Hover: return "Hovering – waiting for operator";
            case BehaviourState.Search: return "Searching for target";
            case BehaviourState.Follow: return "Following";
            case BehaviourState.LostTarget: return "Target lost: hovering";
            case BehaviourState.Landing: return "Landing" + suffix;
            default: return "FAILSAFE: " + LastReason;
        }
    }
}
