using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

/// <summary>
/// DroneFollow policy: flight state, detector box, previous commands, and
/// accumulated damage. No pixels or world-space target direction enter
/// CollectObservations. Ground truth is used only for training reward shaping.
/// </summary>
public sealed class DroneFollowAgent : Agent
{
    [Header("Follow task")]
    public Transform followTarget;
    public float desiredAltitude = 1.5f;
    public float desiredDistanceMin = 2f;
    public float desiredDistanceMax = 3f;
    public float altitudeRewardWeight = 0.01f;
    public float distanceRewardWeight = 0.01f;
    public float centeringRewardWeight = 0.01f;
    public float smoothActionPenalty = 0.002f;

    [Header("Crash damage")]
    public string groundTag = "Ground";
    public string wallTag = "Wall";
    [Min(0f)] public float safeImpactSpeed = 0.5f;
    [Min(0.1f)] public float severeImpactSpeed = 4f;
    [Range(0f, 1f)] public float maxDamagePerImpact = 0.25f;
    [Range(0f, 1f)] public float terminalDamage = 0.85f;
    [Min(0f)] public float crashPenalty = 0.25f;
    [Min(0f)] public float terminalCrashPenalty = 1f;
    [Range(0f, 1f)] public float minimumControlAuthority = 0.4f;
    [Min(0f)] public float impactCooldownSeconds = 0.5f;
    [Min(0f)] public float safeLandingReward = 0.1f;

    IDroneFollowController _controller;
    IDroneTargetDetectionSource _detector;
    Vector3 _spawnPosition;
    Quaternion _spawnRotation;
    Vector4 _previousAction;
    bool _ending;
    bool _actionSizeWarningLogged;
    bool _flightHasStarted;
    bool _landingRequested;
    float _damage;
    float _lastImpactTime = -Mathf.Infinity;

    public override void Initialize()
    {
        MaxStep = Mathf.RoundToInt(60f * DroneFollowContract.DecisionRateHz);
        _spawnPosition = transform.position;
        _spawnRotation = transform.rotation;

        foreach (MonoBehaviour component in GetComponents<MonoBehaviour>())
        {
            if (_controller == null) _controller = component as IDroneFollowController;
            if (_detector == null) _detector = component as IDroneTargetDetectionSource;
        }

        if (_controller == null)
            Debug.LogError("[DroneFollowAgent] No IDroneFollowController is attached.", this);
        if (_detector == null)
            Debug.LogError("[DroneFollowAgent] No target detection source is attached.", this);
        if (followTarget == null)
            Debug.LogError("[DroneFollowAgent] Follow target is not assigned.", this);
    }

    public override void OnEpisodeBegin()
    {
        _ending = false;
        _flightHasStarted = false;
        _landingRequested = false;
        _damage = 0f;
        _lastImpactTime = -Mathf.Infinity;
        _previousAction = Vector4.zero;
        _detector?.ResetDetection();

        if (followTarget != null)
        {
            var targetMotion = followTarget.GetComponent<SimulatedMovingTarget>();
            if (targetMotion != null) targetMotion.ResetEpisode();
        }

        var simulated = _controller as SimulatedDroneFollowControllerAdapter;
        if (simulated != null)
            simulated.ResetForTraining(_spawnPosition, _spawnRotation);
        else if (_controller != null && _controller.State == DroneState.Landed)
            _controller.Takeoff();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        NavdataPacket nav = _controller != null ? _controller.Navdata : null;
        Vector3 gyro = _controller != null ? _controller.BodyAngularVelocityRadSec : Vector3.zero;

        // Proprioception: fixed 10-value layout, shared by sim and hardware adapter.
        sensor.AddObservation(nav != null ? Mathf.Clamp01(nav.altitude / 10f) : 0f);
        sensor.AddObservation(nav != null ? Mathf.Clamp(nav.vx / 5f, -1f, 1f) : 0f);
        sensor.AddObservation(nav != null ? Mathf.Clamp(nav.vy / 5f, -1f, 1f) : 0f);
        sensor.AddObservation(nav != null ? Mathf.Clamp(nav.vz, -1f, 1f) : 0f);
        sensor.AddObservation(nav != null ? Mathf.Clamp(nav.rotX / 180f, -1f, 1f) : 0f);
        sensor.AddObservation(nav != null ? Mathf.Clamp(nav.rotY / 180f, -1f, 1f) : 0f);
        sensor.AddObservation(nav != null ? Mathf.Repeat(nav.rotZ, 360f) / 360f : 0f);
        sensor.AddObservation(Mathf.Clamp(gyro.x / 5f, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(gyro.y / 5f, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(gyro.z / 5f, -1f, 1f));

        // Detector output: normalized box center x/y, image-height fraction, visible.
        DroneTargetDetection detection = _detector != null
            ? _detector.Current
            : DroneTargetDetection.None(Time.time);
        if (detection.Visible)
        {
            sensor.AddObservation(Mathf.Clamp(detection.CenterX, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(detection.CenterY, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp01(detection.Height));
            sensor.AddObservation(1f);
        }
        else
        {
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
        }

        // Last command sent, not an estimate of delayed physical response.
        sensor.AddObservation(_previousAction.x);
        sensor.AddObservation(_previousAction.y);
        sensor.AddObservation(_previousAction.z);
        sensor.AddObservation(_previousAction.w);
        sensor.AddObservation(_damage);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (_ending || _controller == null) return;
        if (actions.ContinuousActions.Length != DroneFollowContract.ActionSize ||
            actions.DiscreteActions.Length != DroneFollowContract.DiscreteBranchSize)
        {
            if (!_actionSizeWarningLogged)
            {
                Debug.LogError($"[DroneFollowAgent] Expected {DroneFollowContract.ActionSize} continuous actions and {DroneFollowContract.DiscreteBranchSize} discrete branch, got {actions.ContinuousActions.Length} and {actions.DiscreteActions.Length}. Re-run DroneDetect > Configure Active Scene for DroneFollow.", this);
                _actionSizeWarningLogged = true;
            }
            return;
        }

        if (_controller.State == DroneState.Landed)
        {
            if (_landingRequested)
                FinishSafeLanding();
            else
            {
                _controller.Takeoff();
                _previousAction = Vector4.zero;
            }
            return;
        }

        if (actions.DiscreteActions[0] == DroneFollowContract.LandAction &&
            (_controller.State == DroneState.Hovering || _controller.State == DroneState.Flying))
        {
            _controller.Land();
            _landingRequested = true;
            _previousAction = Vector4.zero;
            return;
        }

        Vector4 previousAction = _previousAction;
        var ca = actions.ContinuousActions;
        float controlAuthority = Mathf.Lerp(1f, minimumControlAuthority, _damage);
        float forward = Mathf.Clamp(ca[0], -1f, 1f) * controlAuthority;
        float left = Mathf.Clamp(ca[1], -1f, 1f) * controlAuthority;
        float up = Mathf.Clamp(ca[2], -1f, 1f) * controlAuthority;
        float turn = Mathf.Clamp(ca[3], -1f, 1f) * controlAuthority;

        if (_controller.State == DroneState.Hovering || _controller.State == DroneState.Flying)
        {
            // Keep the canonical (forward,left,up,turn) order; do not invert left here.
            _controller.Move(forward, left, up, turn);
            _previousAction = new Vector4(forward, left, up, turn);
        }
        else
        {
            _previousAction = Vector4.zero;
        }

        AddFollowRewards(previousAction, _previousAction);
    }

    void AddFollowRewards(Vector4 previousAction, Vector4 currentAction)
    {
        if (followTarget != null)
        {
            float distance = Vector3.Distance(transform.position, followTarget.position);
            float distanceError = distance < desiredDistanceMin
                ? desiredDistanceMin - distance
                : distance > desiredDistanceMax ? distance - desiredDistanceMax : 0f;
            AddReward(-distanceRewardWeight * distanceError);
        }

        DroneTargetDetection detection = _detector != null
            ? _detector.Current
            : DroneTargetDetection.None(Time.time);
        if (detection.Visible)
        {
            float centerError = Mathf.Abs(detection.CenterX) + Mathf.Abs(detection.CenterY);
            AddReward(0.005f - centeringRewardWeight * centerError);
        }
        else
        {
            AddReward(-0.001f);
        }

        if (_controller.Navdata != null)
            AddReward(-altitudeRewardWeight * Mathf.Abs(_controller.Navdata.altitude - desiredAltitude));

        Vector4 delta = currentAction - previousAction;
        float actionDeltaSquared = delta.x * delta.x + delta.y * delta.y +
                                   delta.z * delta.z + delta.w * delta.w;
        AddReward(-smoothActionPenalty * actionDeltaSquared);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var ca = actionsOut.ContinuousActions;
        var da = actionsOut.DiscreteActions;
        da[0] = 0;
        for (int i = 0; i < ca.Length; i++)
            ca[i] = 0f;
    }

    void OnCollisionEnter(Collision collision)
    {
        if (_ending || collision == null || !_flightHasStarted || _landingRequested ||
            _controller == null || _controller.State == DroneState.Landing ||
            _controller.State == DroneState.Landed)
            return;

        bool isGround = !string.IsNullOrEmpty(groundTag) && collision.gameObject.CompareTag(groundTag);
        bool isWall = !string.IsNullOrEmpty(wallTag) && collision.gameObject.CompareTag(wallTag);
        if (!isGround && !isWall) return;

        ApplyImpactDamage(collision.relativeVelocity.magnitude, collision.gameObject.name);
    }

    void FixedUpdate()
    {
        if (_ending || _controller == null) return;

        if (_controller.State == DroneState.Hovering || _controller.State == DroneState.Flying)
            _flightHasStarted = true;
        else if (_controller.State == DroneState.Landing)
            _landingRequested = true;

        if (_controller.State == DroneState.Emergency)
        {
            _ending = true;
            AddReward(-terminalCrashPenalty);
            EndEpisode();
        }
        else if (_landingRequested && _controller.State == DroneState.Landed)
        {
            FinishSafeLanding();
        }
    }

    void ApplyImpactDamage(float impactSpeed, string hitObjectName)
    {
        if (Time.time - _lastImpactTime < impactCooldownSeconds) return;
        _lastImpactTime = Time.time;

        float severity = Mathf.InverseLerp(safeImpactSpeed, Mathf.Max(safeImpactSpeed + 0.1f, severeImpactSpeed), impactSpeed);
        if (severity <= 0f) return;

        _damage = Mathf.Clamp01(_damage + severity * maxDamagePerImpact);
        AddReward(-crashPenalty * severity);
        Debug.LogWarning($"[DroneFollowAgent] Impact with {hitObjectName}: {impactSpeed:F2} m/s, severity={severity:F2}, damage={_damage:P0}", this);

        if (_damage >= terminalDamage)
        {
            _ending = true;
            AddReward(-terminalCrashPenalty);
            EndEpisode();
        }
    }

    void FinishSafeLanding()
    {
        if (_ending) return;
        _ending = true;
        AddReward(safeLandingReward);
        EndEpisode();
    }
}
