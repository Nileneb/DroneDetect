using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

/// <summary>
/// DroneFollow policy: vector state plus the attached grayscale depth sensor.
/// CollectObservations supplies flight state, detector box, previous commands,
/// and damage; ground truth is used only for training reward shaping.
/// Training: the target walks a fixed <see cref="DroneFollowPath"/> loop from the pad and back; the drone
/// earns gate rewards on the way and is landed automatically once the target is home.
/// In deployment mode (real drone) episodes never end, the operator owns
/// takeoff/landing, and no ground truth or rewards are used.
/// </summary>
public sealed class DroneFollowAgent : Agent
{
    public DroneFollowSentisPerception perception;
    [Tooltip("Real-drone operation: no episode limit, no rewards, takeoff/landing stay with the operator.")]
    public bool deploymentMode;
    [Tooltip("Demo recording only: Heuristic() flies a ground-truth follow controller instead of reading the gamepad.")]
    public bool useScriptedExpert;

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
    [Tooltip("Episode ends early once the drone is farther than this from the target while flying. Random wandering far from the target teaches nothing; a fresh episode starts near it.")]
    [Min(1f)] public float abortDistance = 8f;
    [Tooltip("One-off penalty when an episode is aborted for distance.")]
    [Min(0f)] public float abortDistancePenalty = 1f;
    [Tooltip("Paid on touchdown after the target walked its whole loop, scaled by the share of gates passed well.")]
    [Min(0f)] public float flightCompletionReward = 0.75f;
    [Tooltip("Training resets yaw the drone randomly by up to this angle so the target does not always start centered.")]
    [Range(0f, 90f)] public float spawnYawJitterDegrees = 30f;

    [Header("Paths and gates")]
    [Tooltip("Walking loops; an episode picks one whose difficulty is at most the curriculum stage.")]
    public DroneFollowPath[] paths;
    [Tooltip("Largest single reward: passing the next gate with the person in view at follow distance.")]
    [Min(0f)] public float gateReward = 0.5f;
    public Vector2 walkSpeedRange = new Vector2(0.6f, 1.2f);
    [Tooltip("Target walk speed on curriculum stage 0 (flat path). Slower so the first lesson is learnable.")]
    public Vector2 firstStageWalkSpeedRange = new Vector2(0.3f, 0.5f);
    [Tooltip("Seconds the drone keeps following the standing target before the sim lands it.")]
    [Min(0f)] public float hoverAfterTargetStopsSeconds = 3f;

    [Header("Crash damage")]
    public string groundTag = "Ground";
    public string wallTag = "Wall";
    [Tooltip("Rocks and trees scattered on the terrain.")]
    public string obstacleTag = "Obstacle";
    [Min(0f)] public float safeImpactSpeed = 0.5f;
    [Min(0.1f)] public float severeImpactSpeed = 4f;
    [Range(0f, 1f)] public float maxDamagePerImpact = 0.25f;
    [Range(0f, 1f)] public float terminalDamage = 0.85f;
    [Min(0f)] public float crashPenalty = 1f;
    [Min(0f)] public float damagePenaltyWeight = 0.75f;
    [Min(0f)] public float terminalCrashPenalty = 2.5f;
    [Range(0f, 1f)] public float minimumControlAuthority = 0.4f;
    [Min(0f)] public float impactCooldownSeconds = 0.5f;

    IDroneController _controller;
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
    // WHY(bug, timing): DecisionRequester.TakeActionsBetweenDecisions calls OnActionReceived every physics
    // step (50 Hz), not at the 10 Hz decision rate, so counting calls made episodes 5x shorter than intended.
    float _episodeFlightSeconds;
    int _expertEpisodes;
    float _episodeTimeLimitSeconds;
    float _expertPreviousDistance = -1f;
    int _curriculumStage;
    bool _finalCurriculumStageCompleted;
    bool _hasAttitudeSample;
    Vector3 _previousAttitudeDeg;
    float _previousAttitudeTime;
    bool _hasHeadingReference;
    float _headingReferenceDeg;

    const float VisibilityWindowSeconds = 1f;
    bool[] _visibleHistory = new bool[1];
    int _visibleHistoryIndex;
    DroneFollowPathWalker _walker;
    DroneFollowPath _activePath;
    int[] _visitOrder;
    int _nextGateStep;
    int _gatesPassedWell;
    int _gateCount;
    float _targetFinishedTime = -1f;

    public int CurrentCurriculumStage => _curriculumStage;
    public float CurrentEpisodeFlightSeconds => _episodeFlightSeconds;
    public float CurrentEpisodeTimeLimitSeconds => _episodeTimeLimitSeconds;
    public int GatesPassedWell => _gatesPassedWell;
    public int GateCount => _gateCount;
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

            if (_controller == null) _controller = component as IDroneController;
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
            Debug.LogError("[DroneFollowAgent] No IDroneController is attached.", this);
        if (_detector == null)
            Debug.LogError("[DroneFollowAgent] No target detection source is attached.", this);
        if (followTarget == null && !deploymentMode)
            Debug.LogError("[DroneFollowAgent] Follow target is not assigned.", this);
        if (!deploymentMode && followTarget != null)
        {
            _walker = followTarget.GetComponent<DroneFollowPathWalker>();
            if (_walker == null)
                Debug.LogError("[DroneFollowAgent] The follow target has no DroneFollowPathWalker. Re-run DroneDetect > Configure Active Scene for DroneFollow.", this);
            if (paths == null || paths.Length == 0)
                Debug.LogError("[DroneFollowAgent] No DroneFollowPath assigned. Run DroneDetect > DroneFollow > Build Terrain, Paths and Gates, then Configure Active Scene.", this);
        }

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

        _visibleHistory = new bool[Mathf.Max(1, Mathf.RoundToInt(VisibilityWindowSeconds / Time.fixedDeltaTime))];

        if (!deploymentMode)
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
        _episodeFlightSeconds = 0f;
        _hasAttitudeSample = false;
        _hasHeadingReference = false;
        _detector?.ResetDetection();
        if (deploymentMode)
            return;

        // Expert demos walk the paths in turn (flat first) so a short recording still covers several loops.
        _curriculumStage = useScriptedExpert
            ? _expertEpisodes++ % DroneFollowContract.CurriculumStages
            : Mathf.Clamp(
                Mathf.RoundToInt(Academy.Instance.EnvironmentParameters.GetWithDefault("follow_stage", 0f)),
                0, DroneFollowContract.CurriculumStages - 1);
        _expertPreviousDistance = -1f;
        System.Array.Clear(_visibleHistory, 0, _visibleHistory.Length);
        _visibleHistoryIndex = 0;
        _nextGateStep = 0;
        _gatesPassedWell = 0;
        _targetFinishedTime = -1f;

        _activePath = PickPath(_curriculumStage, exactDifficulty: useScriptedExpert);
        if (_activePath == null || _walker == null)
        {
            Debug.LogError($"[DroneFollowAgent] Cannot start stage {_curriculumStage}: no eligible path or no walker on the target.", this);
            _episodeTimeLimitSeconds = 0f;
            _gateCount = 0;
            return;
        }

        bool reverse = Random.value < 0.5f;
        float stageProgress = _curriculumStage / (float)(DroneFollowContract.CurriculumStages - 1);
        float maxSpeed = Mathf.Lerp(Mathf.Lerp(walkSpeedRange.x, walkSpeedRange.y, 0.5f), walkSpeedRange.y, stageProgress);
        float speed = _curriculumStage == 0
            ? Random.Range(firstStageWalkSpeedRange.x, firstStageWalkSpeedRange.y)
            : Random.Range(walkSpeedRange.x, maxSpeed);
        foreach (DroneFollowPath path in paths)
            if (path != null)
                path.SetGatesActive(path == _activePath);
        _walker.Begin(_activePath, reverse, speed);
        _visitOrder = _activePath.VisitOrder(reverse);
        _gateCount = Mathf.Max(0, _visitOrder.Length - 2);

        // WHY(spec, episode length): the episode follows the walk, not a fixed clock; this limit is only a
        // safety stop (pauses, landing, margin) in case the drone never lands.
        float walkSeconds = _activePath.Length / speed;
        _episodeTimeLimitSeconds = (walkSeconds + 20f + hoverAfterTargetStopsSeconds) * 1.3f;

        if (_controller is SimulatedDroneController simulated)
        {
            Vector3 toTarget = Vector3.ProjectOnPlane(followTarget.position - _spawnPosition, Vector3.up);
            Quaternion facing = toTarget.sqrMagnitude > 0.01f ? Quaternion.LookRotation(toTarget) : _spawnRotation;
            Quaternion spawnYaw = Quaternion.Euler(0f, Random.Range(-spawnYawJitterDegrees, spawnYawJitterDegrees), 0f);
            simulated.ResetAndTakeoff(_spawnPosition, spawnYaw * facing);
        }
        else if (_controller != null && _controller.State == DroneState.Landed)
            _controller.Takeoff();

        Debug.Log(
            $"[DroneFollowAgent] Episode start: controller=" +
            $"{(_controller != null ? _controller.GetType().Name : "missing")}, " +
            $"state={stateBeforeReset}->{(_controller != null ? _controller.State : DroneState.Emergency)}, " +
            $"position={transform.position}, timeLimit={_episodeTimeLimitSeconds:F0}s, " +
            $"stage={_curriculumStage}, path={(_activePath != null ? _activePath.name : "none")}, gates={_gateCount}.",
            this);
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        NavdataPacket nav = _controller != null ? _controller.Navdata : null;
        Vector3 gyro = EstimateBodyRatesRadSec(nav);
        float relativeYaw = RelativeHeadingDeg(nav);

        // Proprioception: fixed 10-value layout, shared by sim and hardware adapter.
        sensor.AddObservation(nav != null ? Mathf.Clamp01(nav.altitude / 10f) : 0f);
        sensor.AddObservation(nav != null ? Mathf.Clamp(nav.vx / 5f, -1f, 1f) : 0f);
        sensor.AddObservation(nav != null ? Mathf.Clamp(nav.vy / 5f, -1f, 1f) : 0f);
        sensor.AddObservation(nav != null ? Mathf.Clamp(nav.vz, -1f, 1f) : 0f);
        sensor.AddObservation(nav != null ? Mathf.Clamp(nav.rotX / 180f, -1f, 1f) : 0f);
        sensor.AddObservation(nav != null ? Mathf.Clamp(nav.rotY / 180f, -1f, 1f) : 0f);
        sensor.AddObservation(relativeYaw / 180f);
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

    // WHY(sim-to-real, gyro): AR.Drone demo navdata has no gyro, so sim and hardware both differentiate the
    // reported attitude at the decision rate; identical estimation keeps the observation distribution identical.
    Vector3 EstimateBodyRatesRadSec(NavdataPacket nav)
    {
        if (nav == null)
            return Vector3.zero;

        var attitude = new Vector3(nav.rotY, nav.rotZ, nav.rotX);
        Vector3 rates = Vector3.zero;
        float dt = Time.time - _previousAttitudeTime;
        if (_hasAttitudeSample && dt > 1e-3f)
        {
            rates = new Vector3(
                Mathf.DeltaAngle(_previousAttitudeDeg.x, attitude.x),
                Mathf.DeltaAngle(_previousAttitudeDeg.y, attitude.y),
                Mathf.DeltaAngle(_previousAttitudeDeg.z, attitude.z)) * (Mathf.Deg2Rad / dt);
        }

        _previousAttitudeDeg = attitude;
        _previousAttitudeTime = Time.time;
        _hasAttitudeSample = true;
        return rates;
    }

    // WHY(sim-to-real, heading): the real drone's yaw is only meaningful relative to where it took off,
    // so the policy sees heading relative to the first in-flight observation of the episode.
    float RelativeHeadingDeg(NavdataPacket nav)
    {
        if (nav == null)
            return 0f;

        bool airborne = _controller.State == DroneState.Hovering || _controller.State == DroneState.Flying;
        if (!_hasHeadingReference && airborne)
        {
            _headingReferenceDeg = nav.rotZ;
            _hasHeadingReference = true;
        }

        return _hasHeadingReference ? Mathf.DeltaAngle(_headingReferenceDeg, nav.rotZ) : 0f;
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (_ending || _controller == null) return;
        if (actions.ContinuousActions.Length != DroneFollowContract.ActionSize ||
            actions.DiscreteActions.Length != 0)
        {
            if (!_actionSizeWarningLogged)
            {
                Debug.LogError($"[DroneFollowAgent] Expected {DroneFollowContract.ActionSize} continuous and no discrete actions, got {actions.ContinuousActions.Length} and {actions.DiscreteActions.Length}. Re-run DroneDetect > Configure Active Scene for DroneFollow.", this);
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
                $"gates={_gatesPassedWell}/{_gateCount}, position={transform.position}.",
                this);
        }

        if (deploymentMode)
        {
            ApplyDeploymentAction(actions.ContinuousActions);
            return;
        }

        if (_controller.State == DroneState.Landed)
        {
            if (_landingRequested)
                FinishLanding();
            else
            {
                _controller.Takeoff();
                _previousAction = Vector4.zero;
            }
            return;
        }

        if (_controller.State != DroneState.Hovering && _controller.State != DroneState.Flying)
            return;

        _episodeFlightSeconds += Time.fixedDeltaTime;

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
        if (_ending)
            return;

        if (!deploymentMode && followTarget != null &&
            (_controller.State == DroneState.Hovering || _controller.State == DroneState.Flying) &&
            Vector3.Distance(transform.position, followTarget.position) > abortDistance)
        {
            Debug.Log($"[DroneFollowAgent] Abbruch: Abstand > {abortDistance:F1} m, Episode endet früh.", this);
            _ending = true;
            AddReward(-abortDistancePenalty);
            EndEpisode();
            return;
        }

        if (_walker != null && _walker.Finished)
        {
            if (_targetFinishedTime < 0f)
                _targetFinishedTime = Time.time;
            else if (Time.time - _targetFinishedTime >= hoverAfterTargetStopsSeconds)
            {
                _controller.Land();
                _landingRequested = true;
                _previousAction = Vector4.zero;
                return;
            }
        }

        if (_episodeFlightSeconds >= _episodeTimeLimitSeconds)
        {
            Debug.LogWarning($"[DroneFollowAgent] Safety timeout after {_episodeFlightSeconds:F0} s; target finished={_walker != null && _walker.Finished}, steps reached={(_walker != null ? _walker.StepsReached : 0)}.", this);
            _ending = true;
            EndEpisode();
        }
    }

    DroneFollowPath PickPath(int stage, bool exactDifficulty)
    {
        if (paths == null)
            return null;
        int eligible = 0;
        DroneFollowPath chosen = null;
        foreach (DroneFollowPath path in paths)
        {
            if (path == null || path.difficulty > stage || path.Waypoints.Count < 2 ||
                (exactDifficulty && path.difficulty != stage))
                continue;
            eligible++;
            if (Random.Range(0, eligible) == 0)
                chosen = path;
        }
        return chosen;
    }

    float VisibleFraction
    {
        get
        {
            int visible = 0;
            foreach (bool sample in _visibleHistory)
                if (sample) visible++;
            return visible / (float)_visibleHistory.Length;
        }
    }

    /// <summary>Called by a gate trigger; only gates the target has already walked through count, in order.</summary>
    public void OnGateEntered(DroneFollowGate gate)
    {
        if (deploymentMode || _ending || _landingRequested || _activePath == null || gate.path != _activePath ||
            _visitOrder == null || _walker == null)
            return;

        // Gate steps are visit-order positions 1.._gateCount; a later gate may be taken (skipped ones earn
        // nothing), an earlier one is ignored. The target must already have reached it: the drone trails.
        int matchedStep = -1;
        for (int step = _nextGateStep; step < _gateCount; step++)
            if (_visitOrder[step + 1] == gate.waypointIndex)
            {
                matchedStep = step;
                break;
            }
        if (matchedStep < 0 || _walker.StepsReached < matchedStep + 1)
            return;

        _nextGateStep = matchedStep + 1;
        DroneTargetDetection detection = _detector != null ? _detector.Current : DroneTargetDetection.None(Time.time);
        float distance = Vector3.Distance(transform.position, followTarget.position);
        float value = DroneFollowRewards.GateReward(
            VisibleFraction, detection.Visible, distance, desiredDistanceMin, desiredDistanceMax);
        if (value > 0f)
            _gatesPassedWell++;
        AddReward(gateReward * value);
    }

    void ApplyDeploymentAction(ActionSegment<float> ca)
    {
        if (_controller.State != DroneState.Hovering && _controller.State != DroneState.Flying)
        {
            _previousAction = Vector4.zero;
            return;
        }

        // The landing branch is ignored on hardware: landing stays an operator decision.
        _previousAction = new Vector4(
            Mathf.Clamp(ca[0], -1f, 1f),
            Mathf.Clamp(ca[1], -1f, 1f),
            Mathf.Clamp(ca[2], -1f, 1f),
            Mathf.Clamp(ca[3], -1f, 1f));
        _controller.Move(_previousAction.x, _previousAction.y, _previousAction.z, _previousAction.w);
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
            distanceError = DroneFollowRewards.DistanceError(distance, desiredDistanceMin, desiredDistanceMax);
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

        _visibleHistory[_visibleHistoryIndex] = detection.Visible;
        _visibleHistoryIndex = (_visibleHistoryIndex + 1) % _visibleHistory.Length;

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

    const float StickDeadzone = 0.1f;

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var ca = actionsOut.ContinuousActions;
        if (ca.Length != DroneFollowContract.ActionSize || _controller == null)
            return;

        if (useScriptedExpert)
        {
            WriteExpertAction(ca);
            return;
        }

        // WHY(demo quality): analog sticks and triggers give graded commands; keyboard keys are only -1/0/+1
        // and the policy imitates that bang-bang flying. Game-style layout: left stick = move, right stick
        // = look (yaw), R2 = climb, L2 = descend.
        Gamepad pad = Gamepad.current;
        if (pad != null)
        {
            Vector2 move = Deadzone(pad.leftStick.ReadValue());
            Vector2 look = Deadzone(pad.rightStick.ReadValue());
            ca[0] = move.y;
            ca[1] = -move.x;
            ca[2] = pad.rightTrigger.ReadValue() - pad.leftTrigger.ReadValue();
            ca[3] = look.x;
            return;
        }

        // WHY(project settings): the editor runs with Input System-only handling, where UnityEngine.Input throws.
        Keyboard keys = Keyboard.current;
        if (keys == null)
            return;
        ca[0] = Axis(keys.wKey.isPressed || keys.upArrowKey.isPressed, keys.sKey.isPressed || keys.downArrowKey.isPressed);
        ca[1] = Axis(keys.aKey.isPressed || keys.leftArrowKey.isPressed, keys.dKey.isPressed || keys.rightArrowKey.isPressed);
        ca[2] = Axis(keys.spaceKey.isPressed, keys.leftShiftKey.isPressed);
        ca[3] = Axis(keys.eKey.isPressed, keys.qKey.isPressed);
    }

    static float Axis(bool positive, bool negative) => (positive ? 1f : 0f) - (negative ? 1f : 0f);

    static Vector2 Deadzone(Vector2 stick)
    {
        float magnitude = stick.magnitude;
        if (magnitude < StickDeadzone)
            return Vector2.zero;
        return stick * (Mathf.InverseLerp(StickDeadzone, 1f, Mathf.Min(1f, magnitude)) / magnitude);
    }

    // Ground-truth PD follower used only to record demonstrations; the trained policy never sees ground truth.
    void WriteExpertAction(ActionSegment<float> ca)
    {
        NavdataPacket nav = _controller.Navdata;
        if (followTarget == null || nav == null)
            return;

        Vector3 toTarget = followTarget.position - transform.position;
        Vector3 flatForward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        Vector3 flatToTarget = Vector3.ProjectOnPlane(toTarget, Vector3.up);
        float distance = flatToTarget.magnitude;
        float bearingDeg = Vector3.SignedAngle(flatForward, flatToTarget, Vector3.up);

        float desiredDistance = 0.5f * (desiredDistanceMin + desiredDistanceMax);
        float rangeRate = _expertPreviousDistance >= 0f
            ? (distance - _expertPreviousDistance) * DroneFollowContract.DecisionRateHz
            : 0f;
        _expertPreviousDistance = distance;

        // Only close distance once the target is roughly ahead; otherwise turn first.
        float alignment = Mathf.Clamp01(1f - Mathf.Abs(bearingDeg) / 60f);
        ca[0] = Mathf.Clamp((0.45f * (distance - desiredDistance) + 0.35f * rangeRate) * alignment, -1f, 1f);
        ca[1] = Mathf.Clamp(0.3f * nav.vy, -1f, 1f);
        ca[2] = Mathf.Clamp(1.5f * (desiredAltitude - nav.altitude), -1f, 1f);
        ca[3] = Mathf.Clamp(bearingDeg / 25f, -1f, 1f);
    }

    void OnCollisionEnter(Collision collision)
    {
        if (deploymentMode || _ending || collision == null || !_flightHasStarted || _landingRequested ||
            _controller == null || _controller.State == DroneState.Landing ||
            _controller.State == DroneState.Landed)
            return;

        bool isGround = !string.IsNullOrEmpty(groundTag) && collision.gameObject.CompareTag(groundTag);
        bool isWall = !string.IsNullOrEmpty(wallTag) && collision.gameObject.CompareTag(wallTag);
        bool isObstacle = !string.IsNullOrEmpty(obstacleTag) && collision.gameObject.CompareTag(obstacleTag);
        if (!isGround && !isWall && !isObstacle) return;

        ApplyImpactDamage(collision.relativeVelocity.magnitude, collision.gameObject.name);
    }

    void FixedUpdate()
    {
        if (deploymentMode || _ending || _controller == null) return;

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
            FinishLanding();
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

    void FinishLanding()
    {
        if (_ending) return;
        _ending = true;
        if (_walker != null && _walker.Finished && _gateCount > 0)
        {
            float gateFraction = _gatesPassedWell / (float)_gateCount;
            AddReward(flightCompletionReward * gateFraction * Mathf.Lerp(1f, 0.5f, _damage));
            if (_curriculumStage == DroneFollowContract.CurriculumStages - 1 && gateFraction >= 0.75f)
                _finalCurriculumStageCompleted = true;
        }
        EndEpisode();
    }
}
