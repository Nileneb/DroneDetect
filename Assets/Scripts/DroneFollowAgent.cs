using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

/// <summary>
/// DroneFollow policy: vector state plus the attached grayscale depth sensor.
/// CollectObservations supplies flight state, detector box, previous commands,
/// and damage; ground truth is used only for training reward shaping.
/// </summary>
public sealed class DroneFollowAgent : Agent
{
    public DroneFollowSentisPerception perception;

    [Header("Follow task")]
    public Transform followTarget;
    public float desiredAltitude = 1.5f;
    public float desiredDistanceMin = 2f;
    public float desiredDistanceMax = 3f;
    [Min(0f)] public float altitudeRewardWeight = 0.12f;
    [Min(0f)] public float distanceRewardWeight = 0.25f;
    [Min(0f)] public float centeringRewardWeight = 0.15f;
    [Min(0f)] public float centeredTargetReward = 0.03f;
    [Min(0f)] public float followHoldReward = 0.015f;
    [Min(0f)] public float lostDetectionPenalty = 0.005f;
    [Min(0f)] public float smoothActionPenalty = 0.0005f;
    [Min(0f)] public float flightCompletionReward = 0.75f;
    [Range(0f, 1f)] public float safeLandingMinimumFlightFraction = 1f;
    [Min(0f)] public float earlyLandingPenalty = 0.02f;

    [Header("Crash damage")]
    public string groundTag = "Ground";
    public string wallTag = "Wall";
    [Min(0f)] public float safeImpactSpeed = 0.5f;
    [Min(0.1f)] public float severeImpactSpeed = 4f;
    [Range(0f, 1f)] public float maxDamagePerImpact = 0.25f;
    [Range(0f, 1f)] public float terminalDamage = 0.85f;
    [Min(0f)] public float crashPenalty = 1f;
    [Min(0f)] public float damagePenaltyWeight = 0.75f;
    [Min(0f)] public float terminalCrashPenalty = 2.5f;
    [Range(0f, 1f)] public float minimumControlAuthority = 0.4f;
    [Min(0f)] public float impactCooldownSeconds = 0.5f;
    [Min(0f)] public float safeLandingReward = 0.5f;

    IDroneFollowController _controller;
    IDroneTargetDetectionSource _detector;
    DroneFollowSentisPerception _perception;
    Vector3 _spawnPosition;
    Quaternion _spawnRotation;
    Vector4 _previousAction;
    bool _ending;
    bool _actionSizeWarningLogged;
    bool _flightHasStarted;
    bool _landingRequested;
    bool _hasPreviousFollowState;
    float _nextActionDiagnosticTime;
    float _damage;
    float _lastImpactTime = -Mathf.Infinity;
    float _previousDistanceError;
    float _previousCenterError;
    float _previousAltitudeError;
    int _episodeFlightDecisions;
    int _episodeDecisionLimit;
    int _curriculumStage;
    bool _finalCurriculumStageCompleted;

    public int CurrentCurriculumStage => _curriculumStage;
    public int CurrentEpisodeFlightDecisions => _episodeFlightDecisions;
    public int CurrentEpisodeDecisionLimit => _episodeDecisionLimit;
    public bool FinalCurriculumStageCompleted => _finalCurriculumStageCompleted;
    public float TargetCenteringQuality
    {
        get
        {
            DroneTargetDetection detection = _detector != null
                ? _detector.Current
                : DroneTargetDetection.None(Time.time);
            return detection.Visible
                ? Mathf.Clamp01(1f - (Mathf.Abs(detection.CenterX) + Mathf.Abs(detection.CenterY)) / 0.5f)
                : 0f;
        }
    }

    public override void Initialize()
    {
        MaxStep = 0;
        _spawnPosition = transform.position;
        _spawnRotation = transform.rotation;

        foreach (MonoBehaviour component in GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (component == null)
                continue;

            if (_controller == null) _controller = component as IDroneFollowController;
            if (_detector == null &&
                component.isActiveAndEnabled &&
                component is IDroneTargetDetectionSource detectionSource)
                _detector = detectionSource;
            if (_perception == null)
                _perception = component as DroneFollowSentisPerception;
        }

        if (perception != null)
        {
            _perception = perception;
            _detector = perception;
        }

        if (_controller == null)
            Debug.LogError("[DroneFollowAgent] No IDroneFollowController is attached.", this);
        if (_detector == null)
            Debug.LogError("[DroneFollowAgent] No target detection source is attached.", this);
        if (followTarget == null)
            Debug.LogError("[DroneFollowAgent] Follow target is not assigned.", this);

        RenderTextureSensorComponent depthSensor = GetComponent<RenderTextureSensorComponent>();
        if (_perception == null)
            _perception = GetComponentInChildren<DroneFollowSentisPerception>(true);
        if (depthSensor == null ||
            depthSensor.RenderTexture == null ||
            depthSensor.RenderTexture.width != DroneFollowContract.DepthObservationWidth ||
            depthSensor.RenderTexture.height != DroneFollowContract.DepthObservationHeight ||
            !depthSensor.Grayscale)
        {
            Debug.LogError(
                $"[DroneFollowAgent] Expected a {DroneFollowContract.DepthObservationWidth}x{DroneFollowContract.DepthObservationHeight} RenderTextureSensorComponent on the Agent. Re-run DroneDetect > Configure Active Scene for DroneFollow.",
                this);
        }
        else if (_perception == null || _perception.depthOutput != depthSensor.RenderTexture)
        {
            Debug.LogError(
                "[DroneFollowAgent] The depth sensor is not connected to the Main Camera Sentis depth output. Re-run DroneDetect > Configure Active Scene for DroneFollow.",
                this);
        }

        DroneFollowHUD.CreateHUD(this);
    }

    public override void OnEpisodeBegin()
    {
        DroneState stateBeforeReset = _controller != null
            ? _controller.State
            : DroneState.Emergency;
        _ending = false;
        _flightHasStarted = false;
        _landingRequested = false;
        _damage = 0f;
        _lastImpactTime = -Mathf.Infinity;
        _previousAction = Vector4.zero;
        _hasPreviousFollowState = false;
        _episodeFlightDecisions = 0;

        _curriculumStage = Mathf.Clamp(
            Mathf.RoundToInt(Academy.Instance.EnvironmentParameters.GetWithDefault("follow_stage", 0f)),
            0, 4);
        float[] flightDurationsSeconds = { 60f, 90f, 120f, 180f, 240f };
        _episodeDecisionLimit = Mathf.RoundToInt(
            flightDurationsSeconds[_curriculumStage] * DroneFollowContract.DecisionRateHz);

        DroneFollowDemoRoute demoRoute = followTarget != null
            ? followTarget.GetComponent<DroneFollowDemoRoute>()
            : null;
        if (followTarget != null && (demoRoute == null || !demoRoute.enabled))
        {
            var targetMotion = followTarget.GetComponent<SimulatedMovingTarget>();
            if (targetMotion != null)
            {
                targetMotion.ApplyCurriculumDifficulty(_curriculumStage);
                targetMotion.ResetEpisode();
            }
        }

        _detector?.ResetDetection();

        var simulated = _controller as SimulatedDroneFollowControllerAdapter;
        if (simulated != null)
            simulated.ResetForTraining(_spawnPosition, _spawnRotation);
        else if (_controller != null && _controller.State == DroneState.Landed)
            _controller.Takeoff();

        Debug.Log(
            $"[DroneFollowAgent] Episode start: controller=" +
            $"{(_controller != null ? _controller.GetType().Name : "missing")}, " +
            $"state={stateBeforeReset}->{(_controller != null ? _controller.State : DroneState.Emergency)}, " +
            $"position={transform.position}, episodeDecisionLimit={_episodeDecisionLimit}, " +
            $"stage={_curriculumStage}.",
            this);
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

        if (Time.time >= _nextActionDiagnosticTime)
        {
            _nextActionDiagnosticTime = Time.time + 2f;
            var diagnosticActions = actions.ContinuousActions;
            Debug.Log(
                $"[DroneFollowAgent] Action received: state={_controller.State}, " +
                $"continuous=({diagnosticActions[0]:F2}, {diagnosticActions[1]:F2}, " +
                $"{diagnosticActions[2]:F2}, {diagnosticActions[3]:F2}), " +
                $"land={actions.DiscreteActions[0]}, position={transform.position}.",
                this);
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

        if (_controller.State != DroneState.Hovering && _controller.State != DroneState.Flying)
            return;

        _episodeFlightDecisions++;
        if (actions.DiscreteActions[0] == DroneFollowContract.LandAction)
        {
            int minimumFlightDecisions = Mathf.CeilToInt(
                _episodeDecisionLimit * safeLandingMinimumFlightFraction);
            if (_episodeFlightDecisions < minimumFlightDecisions)
            {
                AddReward(-earlyLandingPenalty);
                return;
            }

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

        _flightHasStarted = true;
        AddFollowRewards(previousAction, _previousAction);
        if (_episodeFlightDecisions >= _episodeDecisionLimit)
        {
            _ending = true;
            if (_curriculumStage == 4)
                _finalCurriculumStageCompleted = true;
            AddReward(flightCompletionReward * Mathf.Lerp(1f, 0.5f, _damage));
            EndEpisode();
        }
    }

    void AddFollowRewards(Vector4 previousAction, Vector4 currentAction)
    {
        DroneTargetDetection detection = _detector != null
            ? _detector.Current
            : DroneTargetDetection.None(Time.time);

        float distanceError = 0f;
        if (followTarget != null)
        {
            float distance = Vector3.Distance(transform.position, followTarget.position);
            distanceError = distance < desiredDistanceMin
                ? desiredDistanceMin - distance
                : distance > desiredDistanceMax ? distance - desiredDistanceMax : 0f;
        }

        float centerError = detection.Visible
            ? Mathf.Abs(detection.CenterX) + Mathf.Abs(detection.CenterY)
            : _hasPreviousFollowState ? _previousCenterError : 0f;
        NavdataPacket nav = _controller.Navdata;
        float altitudeError = nav != null ? Mathf.Abs(nav.altitude - desiredAltitude) : 0f;

        if (_hasPreviousFollowState)
        {
            AddReward(distanceRewardWeight * Mathf.Clamp(
                _previousDistanceError - distanceError, -0.25f, 0.25f));
            AddReward(centeringRewardWeight * Mathf.Clamp(
                _previousCenterError - centerError, -0.5f, 0.5f));
            AddReward(altitudeRewardWeight * Mathf.Clamp(
                _previousAltitudeError - altitudeError, -0.25f, 0.25f));
        }
        _previousDistanceError = distanceError;
        _previousCenterError = centerError;
        _previousAltitudeError = altitudeError;
        _hasPreviousFollowState = true;

        if (detection.Visible)
        {
            float centeredness = Mathf.Clamp01(1f - centerError / 0.5f);
            AddReward(centeredTargetReward * centeredness);

            bool inDistanceBand = distanceError <= 0.25f;
            bool atAltitude = altitudeError <= 0.5f;
            if (inDistanceBand && atAltitude && centerError <= 0.5f)
                AddReward(followHoldReward);
        }
        else
        {
            AddReward(-lostDetectionPenalty);
        }

        Vector4 delta = currentAction - previousAction;
        float actionDeltaSquared = delta.x * delta.x + delta.y * delta.y +
                                   delta.z * delta.z + delta.w * delta.w;
        AddReward(-smoothActionPenalty * actionDeltaSquared);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var ca = actionsOut.ContinuousActions;
        var da = actionsOut.DiscreteActions;
        da[0] = Input.GetKeyDown(KeyCode.G)
            ? DroneFollowContract.LandAction
            : 0;
        if (ca.Length != DroneFollowContract.ActionSize || _controller == null)
            return;

        ca[0] = Input.GetAxisRaw("Vertical");
        ca[1] = -Input.GetAxisRaw("Horizontal");
        ca[2] = Input.GetKey(KeyCode.Space)
            ? 1f
            : Input.GetKey(KeyCode.LeftShift) ? -1f : 0f;
        ca[3] = Input.GetKey(KeyCode.Q)
            ? -1f
            : Input.GetKey(KeyCode.E) ? 1f : 0f;
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

        float damageBeforeImpact = _damage;
        _damage = Mathf.Clamp01(_damage + severity * maxDamagePerImpact);
        float addedDamage = _damage - damageBeforeImpact;
        AddReward(-crashPenalty * severity - damagePenaltyWeight * addedDamage);
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
        float minimumFlightDecisions = _episodeDecisionLimit * safeLandingMinimumFlightFraction;
        if (_episodeFlightDecisions >= minimumFlightDecisions)
        {
            AddReward(safeLandingReward);
            if (_curriculumStage == 4 && _episodeFlightDecisions >= _episodeDecisionLimit)
                _finalCurriculumStageCompleted = true;
        }
        else
            AddReward(-flightCompletionReward);
        EndEpisode();
    }
}
