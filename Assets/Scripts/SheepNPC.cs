using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class SheepNPC : MonoBehaviour
{
    [Header("Behaviour")]
    public float wanderRadius = 8f;
    public float fleeRadius = 6f;
    public float wanderInterval = 3f;

    public bool IsCaught { get; private set; }
    public int SheepId { get; set; }

    NavMeshAgent _agent;
    float _wanderTimer;
    bool _isHost;

    void Awake() => _agent = GetComponent<NavMeshAgent>();

    void Start()
    {
        _isHost = ShepherdGameManager.IsHost;
        _wanderTimer = Random.Range(0f, wanderInterval);

        if (RevbClient.Instance != null)
            RevbClient.Instance.OnEvent += HandleNetEvent;
    }

    void Update()
    {
        if (IsCaught || !_isHost) return;

        var threat = FindNearestThreat();
        if (threat != null)
        {
            var fleeDir = (transform.position - threat.position).normalized;
            var target = transform.position + fleeDir * fleeRadius;
            if (NavMesh.SamplePosition(target, out var hit, fleeRadius, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
        }
        else
        {
            _wanderTimer -= Time.deltaTime;
            if (_wanderTimer <= 0f)
            {
                _wanderTimer = wanderInterval;
                SetRandomDestination();
            }
        }

        BroadcastState();
    }

    Transform FindNearestThreat()
    {
        Transform nearest = null;
        float nearestDist = fleeRadius;
        var wolves = FindObjectsOfType<WolfPlayer>();
        foreach (var w in wolves)
        {
            float d = Vector3.Distance(transform.position, w.transform.position);
            if (d < nearestDist) { nearestDist = d; nearest = w.transform; }
        }
        return nearest;
    }

    void SetRandomDestination()
    {
        var randomDir = Random.insideUnitSphere * wanderRadius;
        randomDir += transform.position;
        if (NavMesh.SamplePosition(randomDir, out var hit, wanderRadius, NavMesh.AllAreas))
            _agent.SetDestination(hit.position);
    }

    float _broadcastTimer;
    void BroadcastState()
    {
        _broadcastTimer -= Time.deltaTime;
        if (_broadcastTimer > 0 || RevbClient.Instance == null) return;
        _broadcastTimer = 0.2f;

        var p = transform.position;
        var json = $"{{\"id\":{SheepId},\"x\":{p.x:F2},\"y\":{p.y:F2},\"z\":{p.z:F2},\"f\":0}}";
        RevbClient.Instance.Send("sheep.state", json);
    }

    void HandleNetEvent(string eventName, string json)
    {
        if (eventName != "sheep.state" || _isHost) return;
        var d = JsonUtility.FromJson<SheepStateData>(json);
        if (d.id != SheepId) return;
        transform.position = Vector3.Lerp(transform.position, new Vector3(d.x, d.y, d.z), 0.3f);
    }

    public void OnCaught()
    {
        IsCaught = true;
        _agent.enabled = false;
        gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        if (RevbClient.Instance != null)
            RevbClient.Instance.OnEvent -= HandleNetEvent;
    }

    [System.Serializable]
    struct SheepStateData { public int id; public float x, y, z; public int f; }
}
