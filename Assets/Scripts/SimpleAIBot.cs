using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Minimal AI-Bot fallback when no human opponent matchmakes within the
/// timeout. Drives a wolf-mesh or drone-mesh based on <see cref="role"/>.
///
/// Wolf-AI: NavMesh-chase the nearest active sheep.
/// Drone-AI: hover near sheep cluster, trigger scarer when any wolf < 7m.
///
/// Spawned by ShepherdGameManager when PlayerPrefs.shepherd_ai_role matches.
/// </summary>
public class SimpleAIBot : MonoBehaviour
{
    public enum Role { Wolf, Drone }

    [Header("Config")]
    public Role role = Role.Wolf;
    public float reactionInterval = 0.25f;
    public float scarerTriggerDistance = 7f;

    [Header("Wolf bot")]
    public float wolfSpeed = 7f;
    [Tooltip("Speed multiplier when a sheep is within huntRange — wolf sprints.")]
    public float wolfSprintMultiplier = 1.5f;
    [Tooltip("Distance under which the wolf locks-on and sprints.")]
    public float huntRange = 10f;
    public float wolfCatchRadius = 2.0f;
    [Tooltip("Switch target if no progress (closing distance) for this many seconds.")]
    public float stuckTimeout = 2.5f;

    [Header("Drone bot")]
    public float droneHoverHeight = 4f;
    public float droneMoveSpeed = 3f;
    public DroneScarer scarer;

    CharacterController _cc;
    NavMeshAgent _navAgent;
    ShepherdGameManager _gm;
    Rigidbody _rb;
    float _nextDecisionAt;

    // Stuck-detection: remember last target + closest distance achieved against it
    Transform _currentTarget;
    float _bestDistToTarget = float.MaxValue;
    float _stuckSince;

    void Start()
    {
        _gm       = FindAnyObjectByType<ShepherdGameManager>();
        _cc       = GetComponent<CharacterController>();
        _navAgent = GetComponent<NavMeshAgent>();
        _rb       = GetComponent<Rigidbody>();
        if (role == Role.Drone && _rb != null) _rb.useGravity = false;
    }

    void Update()
    {
        if (Time.time < _nextDecisionAt || _gm == null) return;
        _nextDecisionAt = Time.time + reactionInterval;

        if (role == Role.Wolf) UpdateWolf();
        else                   UpdateDrone();
    }

    // ── Wolf AI ──────────────────────────────────────────────────────────
    void UpdateWolf()
    {
        // Re-target each tick: pick the closest sheep, but stick to current
        // target if it's still close enough — switching constantly leaves the
        // wolf zig-zagging and never closing in.
        Transform nearest = FindNearestSheep();
        if (nearest == null) return;

        if (_currentTarget != nearest)
        {
            // Allow target swap if (a) no current target, (b) nearest is
            // significantly closer than current target, or (c) stuck for too long.
            float curDist = _currentTarget != null
                ? Vector3.Distance(transform.position, _currentTarget.position)
                : float.MaxValue;
            float newDist = Vector3.Distance(transform.position, nearest.position);
            bool stuck = Time.time - _stuckSince > stuckTimeout;
            if (_currentTarget == null || newDist < curDist * 0.7f || stuck)
            {
                _currentTarget = nearest;
                _bestDistToTarget = newDist;
                _stuckSince = Time.time;
            }
        }

        var target = _currentTarget;
        float distToTarget = Vector3.Distance(transform.position, target.position);

        // Progress tracking — closing distance resets the stuck timer
        if (distToTarget < _bestDistToTarget - 0.1f)
        {
            _bestDistToTarget = distToTarget;
            _stuckSince = Time.time;
        }

        // Sprint when within huntRange — visible "Jagdtrieb"
        float speed = distToTarget < huntRange ? wolfSpeed * wolfSprintMultiplier : wolfSpeed;

        if (_navAgent != null && _navAgent.isOnNavMesh)
        {
            _navAgent.speed = speed;
            _navAgent.SetDestination(target.position);
        }
        else if (_cc != null)
        {
            var dir = (target.position - transform.position);
            dir.y = 0;
            if (dir.sqrMagnitude > 0.0001f)
            {
                var nd = dir.normalized;
                _cc.SimpleMove(nd * speed);
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(nd), 6f * Time.deltaTime);
            }
        }

        // Catch check
        if (distToTarget < wolfCatchRadius)
        {
            var sheep = target.GetComponent<SheepNPC>();
            if (sheep != null && !sheep.IsCaught)
            {
                _gm.OnSheepCaught(sheep);
                _currentTarget = null;
                _bestDistToTarget = float.MaxValue;
            }
        }
    }

    Transform FindNearestSheep()
    {
        Transform nearest = null;
        float best = float.MaxValue;
        foreach (var s in _gm.ActiveSheep)
        {
            if (s == null) continue;
            float d = Vector3.SqrMagnitude(transform.position - s.position);
            if (d < best) { best = d; nearest = s; }
        }
        return nearest;
    }

    // ── Drone AI ─────────────────────────────────────────────────────────
    void UpdateDrone()
    {
        // Hover near the sheep cluster's centroid, react to nearest wolf
        if (_gm.ActiveSheep.Count == 0) return;

        Vector3 centroid = Vector3.zero;
        int n = 0;
        foreach (var s in _gm.ActiveSheep)
        {
            if (s == null) continue;
            centroid += s.position;
            n++;
        }
        if (n == 0) return;
        centroid /= n;
        centroid.y = droneHoverHeight;

        var wolf = FindAnyObjectByType<WolfPlayer>();
        var bots = FindObjectsByType<SimpleAIBot>();
        Transform threat = wolf != null ? wolf.transform : null;
        foreach (var b in bots) if (b != this && b.role == Role.Wolf) { threat = b.transform; break; }

        Vector3 targetPos = threat != null
            ? Vector3.Lerp(centroid, threat.position + Vector3.up * droneHoverHeight, 0.6f)
            : centroid;

        transform.position = Vector3.MoveTowards(transform.position, targetPos, droneMoveSpeed * Time.deltaTime);

        // Trigger scarer
        if (scarer != null && scarer.IsReady && threat != null)
        {
            float d = Vector3.Distance(transform.position, threat.position);
            if (d < scarerTriggerDistance)
                scarer.Activate();
        }
    }
}
