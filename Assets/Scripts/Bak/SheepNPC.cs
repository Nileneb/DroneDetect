using UnityEngine;

/// <summary>
/// Simple wandering sheep. No NavMesh — random XZ-direction that re-rolls
/// every <see cref="changeDirectionInterval"/> seconds. Wolf-proximity
/// ramps <see cref="Fear"/> 0..1 which scales speed from baseSpeed to
/// panicSpeed and biases the wander-direction away from the wolf.
///
/// Per User-Spec: "lass sie einfach in eine RandomVector2 Direktion laufen
/// und mehr angst gleich mehr Geschwindigkeit, ist doch viel simpler".
/// </summary>
public class SheepNPC : MonoBehaviour
{
    [Header("Movement")]
    public float baseSpeed = 1.5f;
    public float panicSpeed = 7f;
    public float changeDirectionInterval = 2.5f;

    [Header("Threat")]
    public float threatRadius = 8f;
    public float fearGainRate = 1.2f;
    public float fearDecayRate = 0.4f;
    [Range(0f, 1f)] public float fleeBias = 0.7f;

    [Header("Arena bounds")]
    public float arenaHalfExtent = 30f;

    public bool IsCaught { get; private set; }
    public int SheepId { get; set; }
    public float Fear { get; private set; }

    Vector3 _direction;
    float _changeTimer;

    void Awake()
    {
        // NavMeshAgent often was attached — disable if present so we drive transform directly
        var nav = GetComponent<UnityEngine.AI.NavMeshAgent>();
        if (nav != null) nav.enabled = false;
        _direction = RandomXZDirection();
    }

    Vector3 RandomXZDirection()
    {
        var v = Random.insideUnitCircle.normalized;
        return new Vector3(v.x, 0f, v.y);
    }

    void Update()
    {
        if (IsCaught) return;

        // Fear ramp from nearest wolf
        var wolfPos = FindNearestWolfPos(out float dist);
        if (wolfPos.HasValue && dist < threatRadius)
            Fear = Mathf.Clamp01(Fear + fearGainRate * Time.deltaTime * (1f - dist / threatRadius));
        else
            Fear = Mathf.Clamp01(Fear - fearDecayRate * Time.deltaTime);

        // Reroll direction periodically; bias flight away from wolf when scared
        _changeTimer -= Time.deltaTime;
        if (_changeTimer <= 0f)
        {
            _changeTimer = changeDirectionInterval * Random.Range(0.7f, 1.3f);
            _direction = RandomXZDirection();
            if (Fear > 0.1f && wolfPos.HasValue)
            {
                var away = (transform.position - wolfPos.Value); away.y = 0; away.Normalize();
                _direction = Vector3.Slerp(_direction, away, fleeBias * Fear).normalized;
            }
        }

        // Bounce inside arena
        var pos = transform.position;
        if (Mathf.Abs(pos.x) > arenaHalfExtent) _direction.x = -Mathf.Sign(pos.x) * Mathf.Abs(_direction.x);
        if (Mathf.Abs(pos.z) > arenaHalfExtent) _direction.z = -Mathf.Sign(pos.z) * Mathf.Abs(_direction.z);

        float speed = Mathf.Lerp(baseSpeed, panicSpeed, Fear);
        transform.position += _direction * speed * Time.deltaTime;
        if (_direction.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(_direction), Time.deltaTime * 5f);
    }

    Vector3? FindNearestWolfPos(out float dist)
    {
        dist = float.MaxValue;
        Vector3? nearest = null;
        var wolves = Object.FindObjectsByType<WolfPlayer>();
        foreach (var w in wolves)
        {
            float d = Vector3.Distance(transform.position, w.transform.position);
            if (d < dist) { dist = d; nearest = w.transform.position; }
        }
        var bots = Object.FindObjectsByType<SimpleAIBot>();
        foreach (var b in bots)
        {
            if (b.role != SimpleAIBot.Role.Wolf) continue;
            float d = Vector3.Distance(transform.position, b.transform.position);
            if (d < dist) { dist = d; nearest = b.transform.position; }
        }
        return nearest;
    }

    public void OnCaught()
    {
        IsCaught = true;
        gameObject.SetActive(false);
    }
}
