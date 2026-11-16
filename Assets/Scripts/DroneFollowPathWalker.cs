using UnityEngine;

/// <summary>
/// Walks the follow target calmly along a <see cref="DroneFollowPath"/> loop, kept on the ground by a
/// downward ray against Ground-tagged colliders. Replaces random roaming: the route is fully fixed.
/// </summary>
public sealed class DroneFollowPathWalker : MonoBehaviour
{
    public string groundTag = "Ground";
    [Tooltip("Chance to stand still briefly on reaching a waypoint.")]
    [Range(0f, 1f)] public float pauseChance = 0.15f;
    public Vector2 pauseSecondsRange = new Vector2(1f, 3f);
    [Min(0f)] public float turnDegreesPerSecond = 180f;

    DroneFollowPath _path;
    int[] _order;
    int _nextStep;
    float _speed;
    float _pauseRemaining;
    bool _finished;
    Animator _animator;
    Animation _legacyAnimation;
    readonly RaycastHit[] _hits = new RaycastHit[16];

    public bool Finished => _finished;
    public bool IsWalking => _path != null && !_finished && _pauseRemaining <= 0f;
    public float Speed => _speed;
    /// <summary>Number of visit-order steps the target has reached (0 = still heading to the first one).</summary>
    public int StepsReached => _order == null ? 0 : _nextStep - 1;

    void Awake()
    {
        _animator = GetComponentInChildren<Animator>();
        _legacyAnimation = GetComponentInChildren<Animation>();
    }

    public void Begin(DroneFollowPath path, bool reverse, float speed)
    {
        _path = path;
        _order = path.VisitOrder(reverse);
        _speed = speed;
        _pauseRemaining = 0f;
        _finished = false;
        _nextStep = 1;
        transform.position = SnapToGround(path.Waypoints[_order[0]]);
        Vector3 heading = Vector3.ProjectOnPlane(path.Waypoints[_order[1]] - transform.position, Vector3.up);
        if (heading.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.LookRotation(heading);
        UpdateAnimation();
    }

    /// <summary>Stops walking in place (test benches); Begin starts a new walk.</summary>
    public void Stop() => _path = null;

    void Update()
    {
        if (_path == null || _finished)
            return;

        if (_pauseRemaining > 0f)
            _pauseRemaining -= Time.deltaTime;
        else
            Walk(Time.deltaTime);
        UpdateAnimation();
    }

    void Walk(float dt)
    {
        Vector3 destination = _path.Waypoints[_order[_nextStep]];
        Vector3 flatToDestination = Vector3.ProjectOnPlane(destination - transform.position, Vector3.up);
        float step = _speed * dt;
        if (flatToDestination.magnitude <= step)
        {
            transform.position = SnapToGround(destination);
            _nextStep++;
            if (_nextStep >= _order.Length)
            {
                _finished = true;
                return;
            }
            if (Random.value < pauseChance)
                _pauseRemaining = Random.Range(pauseSecondsRange.x, pauseSecondsRange.y);
            return;
        }

        Vector3 direction = flatToDestination.normalized;
        transform.position = SnapToGround(transform.position + direction * step);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, Quaternion.LookRotation(direction), turnDegreesPerSecond * dt);
    }

    Vector3 SnapToGround(Vector3 position)
    {
        Vector3 origin = new Vector3(position.x, position.y + 50f, position.z);
        int hitCount = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, 200f, ~0, QueryTriggerInteraction.Ignore);
        float bestDistance = float.MaxValue;
        for (int i = 0; i < hitCount; i++)
        {
            if (!_hits[i].collider.CompareTag(groundTag) || _hits[i].distance >= bestDistance)
                continue;
            bestDistance = _hits[i].distance;
            position.y = _hits[i].point.y;
        }
        if (bestDistance == float.MaxValue)
            Debug.LogError($"[DroneFollowPathWalker] No {groundTag}-tagged collider below {position}; the target keeps its height.", this);
        return position;
    }

    void UpdateAnimation()
    {
        bool walking = IsWalking;
        if (_animator != null && _animator.runtimeAnimatorController != null)
        {
            foreach (AnimatorControllerParameter parameter in _animator.parameters)
                if (parameter.name == "Speed" && parameter.type == AnimatorControllerParameterType.Float)
                    _animator.SetFloat("Speed", walking ? _speed : 0f);
            return;
        }

        if (_legacyAnimation == null)
            return;
        string wanted = walking ? "walk" : "idle";
        foreach (AnimationState state in _legacyAnimation)
        {
            if (!state.name.ToLowerInvariant().Contains(wanted))
                continue;
            if (!_legacyAnimation.IsPlaying(state.name))
                _legacyAnimation.CrossFade(state.name, 0.25f);
            return;
        }
    }
}
