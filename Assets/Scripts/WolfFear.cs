using UnityEngine;

[RequireComponent(typeof(WolfPlayer))]
public class WolfFear : MonoBehaviour
{
    [Header("Fear Settings")]
    public float fearGainRate = 0.5f;
    public float fearDecayRate = 0.12f;
    public float panicDuration = 3.5f;
    public float panicSpeed = 9f;

    public float Fear { get; private set; }
    public bool IsPanicking { get; private set; }

    WolfPlayer _wolf;
    float _panicTimer;
    Vector3 _panicDir;

    void Start() => _wolf = GetComponent<WolfPlayer>();

    void Update()
    {
        if (IsPanicking)
        {
            _panicTimer -= Time.deltaTime;
            _wolf.GetComponent<CharacterController>()
                 .SimpleMove(_panicDir * panicSpeed);
            if (_panicTimer <= 0f)
            {
                IsPanicking = false;
                Fear = 0f;
            }
            return;
        }
        Fear = Mathf.Clamp01(Fear - fearDecayRate * Time.deltaTime);
    }

    public void AddFear(float rate)
    {
        // WHY: dropped the !_wolf.IsLocal guard. In offline mode the wolf is
        // an AI bot (IsLocal=false), so the guard suppressed all fear and the
        // mechanic appeared broken. AI wolves still need to react to the
        // drone's scarer so the demo feels alive.
        if (IsPanicking) return;
        Fear = Mathf.Clamp01(Fear + rate * Time.deltaTime);
        if (Fear >= 1f) TriggerPanic();
    }

    void TriggerPanic()
    {
        IsPanicking = true;
        _panicTimer = panicDuration;
        _panicDir = AwayFromNearestSheep();
    }

    Vector3 AwayFromNearestSheep()
    {
        var gm = FindAnyObjectByType<ShepherdGameManager>();
        if (gm == null || gm.ActiveSheep.Count == 0) return -transform.forward;
        Transform nearest = null;
        float best = float.MaxValue;
        foreach (var s in gm.ActiveSheep)
        {
            float d = Vector3.SqrMagnitude(transform.position - s.position);
            if (d < best) { best = d; nearest = s; }
        }
        var dir = nearest != null ? transform.position - nearest.position : -transform.forward;
        dir.y = 0;
        return dir.normalized;
    }
}
