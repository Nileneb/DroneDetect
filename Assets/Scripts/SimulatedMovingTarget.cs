using UnityEngine;

/// <summary>
/// Simple controllable target motion for the DroneFollow smoke-training scene.
/// Replace the target renderer with a humanoid/GLB later; the detector contract
/// remains unchanged.
/// </summary>
public sealed class SimulatedMovingTarget : MonoBehaviour
{
    [Header("Motion")]
    [Min(0f)] public float roamRadius = 4f;
    [Min(0f)] public float minSpeed = 0.35f;
    [Min(0f)] public float maxSpeed = 1.25f;
    [Range(0f, 1f)] public float stopProbability = 0.25f;
    [Min(0f)] public float minStopSeconds = 0.4f;
    [Min(0f)] public float maxStopSeconds = 1.5f;

    [Header("Scale randomization")]
    [Min(0.1f)] public float minScaleMultiplier = 0.8f;
    [Min(0.1f)] public float maxScaleMultiplier = 1.2f;

    Vector3 _startPosition;
    Vector3 _startScale;
    Vector3 _destination;
    float _speed;
    float _pauseRemaining;
    bool _initialized;

    void Awake()
    {
        CaptureStartPose();
        ResetEpisode();
    }

    void Update()
    {
        if (!_initialized) return;
        if (_pauseRemaining > 0f)
        {
            _pauseRemaining -= Time.deltaTime;
            return;
        }

        transform.position = Vector3.MoveTowards(
            transform.position, _destination, _speed * Time.deltaTime);

        if (Vector3.Distance(transform.position, _destination) < 0.05f)
            ChooseNextLeg();
    }

    void CaptureStartPose()
    {
        if (_initialized) return;
        _startPosition = transform.position;
        _startScale = transform.localScale;
        _initialized = true;
    }

    public void ResetEpisode()
    {
        CaptureStartPose();
        transform.position = _startPosition;
        transform.localScale = _startScale * Random.Range(minScaleMultiplier, maxScaleMultiplier);
        _pauseRemaining = 0f;
        ChooseNextLeg();
    }

    void ChooseNextLeg()
    {
        if (Random.value < stopProbability)
        {
            _pauseRemaining = Random.Range(
                Mathf.Min(minStopSeconds, maxStopSeconds),
                Mathf.Max(minStopSeconds, maxStopSeconds));
            _destination = transform.position;
            return;
        }

        Vector2 offset = Random.insideUnitCircle * roamRadius;
        _destination = new Vector3(
            _startPosition.x + offset.x,
            _startPosition.y,
            _startPosition.z + offset.y);
        _speed = Random.Range(Mathf.Min(minSpeed, maxSpeed), Mathf.Max(minSpeed, maxSpeed));
    }
}
