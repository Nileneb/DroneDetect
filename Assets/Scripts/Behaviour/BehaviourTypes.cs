using UnityEngine;

/// <summary>Behaviour states of the follow mission; see docs/DRONEFOLLOW_BEHAVIOUR.md.</summary>
public enum BehaviourState
{
    Idle,
    Takeoff,
    Hover,
    Search,
    Follow,
    LostTarget,
    Landing,
    Failsafe
}

/// <summary>One-shot request from the operator (UI button or hotkey).</summary>
public enum OperatorCommand
{
    None,
    Takeoff,
    Search,
    Follow,
    Hover,
    Land
}

public struct BehaviourInputs
{
    public DroneState DroneState;
    public float Altitude;
    /// <summary>Raw battery percent as reported by the drone; 0 or less means unknown.</summary>
    public float Battery;
    public bool LinkUp;
    public bool TargetVisible;
    /// <summary>Latest policy output (forward, left, up, turn); only used in Follow.</summary>
    public Vector4 Policy;
    public OperatorCommand Operator;
    /// <summary>The app really left the foreground (home button, call), not just a permission dialog.</summary>
    public bool AppPaused;
}

public struct BehaviourOutput
{
    public BehaviourState State;
    /// <summary>Set only on the tick of a state change.</summary>
    public string TransitionReason;
    public string Status;
    /// <summary>True on the single tick that enters Takeoff.</summary>
    public bool RequestTakeoff;
    /// <summary>True in Landing and Failsafe (repeated every tick; landing is idempotent).</summary>
    public bool RequestLand;
    /// <summary>True when <see cref="Move"/> should be sent; otherwise the drone hovers.</summary>
    public bool RequestMove;
    /// <summary>forward, left, up, turn in [-1, 1].</summary>
    public Vector4 Move;
    public float BatteryFiltered;
}

[System.Serializable]
public sealed class BehaviourSettings
{
    [Min(0.1f)] public float followLostSeconds = 1.5f;
    [Min(0.1f)] public float lostToSearchSeconds = 3f;
    [Min(1f)] public float lostToLandSeconds = 20f;
    [Min(0.1f)] public float acquireSeconds = 0.5f;
    [Min(1f)] public float takeoffTimeoutSeconds = 8f;
    [Range(0.05f, 1f)] public float searchYaw = 0.35f;
    [Range(0.05f, 1f)] public float searchClimb = 0.3f;
    [Min(0.1f)] public float searchClimbSeconds = 1.4f;
    [Min(0.5f)] public float searchMaxAltitude = 2f;
    [Min(5f)] public float searchTimeoutSeconds = 45f;
    [Min(0.5f)] public float maxAltitude = 2.5f;
    [Min(0.2f)] public float linkLostSeconds = 1f;
    [Range(0, 100)] public int landBelowBatteryPercent = 12;
    [Tooltip("The drone's battery reading sags under load; only a value that stays low over this window counts.")]
    [Min(0.5f)] public float batteryWindowSeconds = 5f;
    [Min(0f)] public float batteryDebounceSeconds = 2f;
    /// <summary>Full turn at the configured yaw rate (control_yaw = 100 deg/s at 1.0).</summary>
    public float SearchRevolutionSeconds => 360f / (100f * searchYaw);
}
