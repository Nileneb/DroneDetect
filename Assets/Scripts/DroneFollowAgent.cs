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
    [Header("Follow task")]
    public Transform followTarget;
    public float desiredAltitude = 1.5f;
    public float desiredDistanceMin = 2f;
    public float desiredDistanceMax = 3f;
    [Min(0f)] public float altitudeRewardWeight = 0.12f;
    [Min(0f)] public float distanceRewardWeight = 0.25f;
    [Min(0f)] public float centeringRewardWeight = 0.15f;
    [Min(0f)] public float followHoldReward = 0.015f;
    [Min(0f)] public float lostDetectionPenalty = 0.005f;
    [Min(0f)] public float smoothActionPenalty = 0.0005f;
    [Min(0f)] public float flightCompletionReward = 0.75f;
    [Range(0f, 1f)] public float safeLandingMinimumFlightFraction = 0.8f;
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
    Vector3 _spawnPosition;
    Quaternion _spawnRotation;
    Vector4 _previousAction;
    bool _ending;
    bool _actionSizeWarningLogged;
    bool _flightHasStarted;
    bool _landingRequested;
    bool _hasPreviousFollowState;
    float _damage;
    float _lastImpactTime = -Mathf.Infinity;
    float _previousDistanceError;
    float _previousCenterError;
    float _previousAltitudeError;
    int _episodeFlightDecisions;
    int _episodeDecisionLimit;
    int _curriculumStage;

    public override void Initialize()
    {
        MaxStep = 0;
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

        RenderTextureSensorComponent depthSensor = GetComponent<RenderTextureSensorComponent>();
        DroneDepthCamera depthCamera = GetComponentInChildren<DroneDepthCamera>(true);
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
        else if (depthCamera == null || depthCamera.outputTexture != depthSensor.RenderTexture)
        {
            Debug.LogError(
                "[DroneFollowAgent] The depth sensor is not connected to the Main Camera depth output. Re-run DroneDetect > Configure Active Scene for DroneFollow.",
                this);
        }
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
        float[] flightDurationsSeconds = { 12f, 20f, 30f, 45f, 60f };
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

        var simulatedDetector = _detector as SimulatedBoundingBoxDetector;
        if (simulatedDetector != null)
            simulatedDetector.ApplyCurriculumDifficulty(_curriculumStage);
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
        da[0] = 0;
        if (ca.Length != DroneFollowContract.ActionSize || _controller == null)
            return;

        NavdataPacket nav = _controller.Navdata;
        float altitudeError = nav != null ? desiredAltitude - nav.altitude : 0f;
        DroneTargetDetection detection = _detector != null
            ? _detector.Current
            : DroneTargetDetection.None(Time.time);

        if (!detection.Visible)
        {
            ca[0] = 0f;
            ca[1] = 0f;
            ca[2] = Mathf.Clamp(altitudeError * 0.75f, -0.5f, 0.5f);
            ca[3] = (_episodeFlightDecisions / 20) % 2 == 0 ? 0.25f : -0.25f;
            return;
        }

        float desiredBoxHeight = CalculateDesiredDetectionHeight();
        ca[0] = Mathf.Clamp((desiredBoxHeight - detection.Height) * 2f, -0.5f, 0.5f);
        ca[1] = Mathf.Clamp(-detection.CenterX * 0.7f, -0.5f, 0.5f);
        ca[2] = Mathf.Clamp(altitudeError * 0.75f + detection.CenterY * 0.2f, -0.5f, 0.5f);
        ca[3] = Mathf.Clamp(detection.CenterX * 0.8f, -0.5f, 0.5f);
    }

    float CalculateDesiredDetectionHeight()
    {
        var simulatedDetector = _detector as SimulatedBoundingBoxDetector;
        Camera camera = simulatedDetector != null
            ? simulatedDetector.observationCamera
            : GetComponentInChildren<Camera>();
        if (camera == null || followTarget == null)
            return 0.25f;

        Renderer[] renderers = followTarget.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return 0.25f;

        Bounds targetBounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            targetBounds.Encapsulate(renderers[i].bounds);

        float targetHeight = targetBounds.size.y;
        float desiredDistance = (desiredDistanceMin + desiredDistanceMax) * 0.5f;
        float frustumHeight = 2f * desiredDistance *
            Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        return Mathf.Clamp(targetHeight / Mathf.Max(0.01f, frustumHeight), 0.05f, 0.85f);
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
            AddReward(safeLandingReward);
        else
            AddReward(-flightCompletionReward);
        EndEpisode();
    }
}
