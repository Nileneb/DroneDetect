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
    public float wolfSpeed = 5f;
    public float wolfCatchRadius = 1.2f;

    [Header("Drone bot")]
    public float droneHoverHeight = 4f;
    public float droneMoveSpeed = 3f;
    public DroneScarer scarer;

    CharacterController _cc;
    NavMeshAgent _navAgent;
    ShepherdGameManager _gm;
    Rigidbody _rb;
    float _nextDecisionAt;

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
        Transform target = FindNearestSheep();
        if (target == null) return;

        if (_navAgent != null && _navAgent.isOnNavMesh)
        {
            _navAgent.speed = wolfSpeed;
            _navAgent.SetDestination(target.position);
        }
        else if (_cc != null)
        {
            // Direct move fallback
            var dir = (target.position - transform.position).normalized;
            dir.y = 0;
            _cc.SimpleMove(dir * wolfSpeed);
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(dir);
        }

        // Catch check
        if (Vector3.Distance(transform.position, target.position) < wolfCatchRadius)
        {
            var sheep = target.GetComponent<SheepNPC>();
            if (sheep != null && !sheep.IsCaught)
                _gm.OnSheepCaught(sheep);
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
        var bots = FindObjectsByType<SimpleAIBot>(FindObjectsSortMode.None);
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
