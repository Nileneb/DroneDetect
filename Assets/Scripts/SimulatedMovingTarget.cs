using UnityEngine;

/// <summary>
/// Simple controllable target motion for the DroneFollow smoke-training scene.
/// Replace the target renderer with a humanoid/GLB later; the detector contract
/// remains unchanged.
/// </summary>
public sealed class SimulatedMovingTarget : MonoBehaviour
{
    [Header("Motion")]
    [Min(0f)] public float roamRadius = 12f;
    [Min(0f)] public float minSpeed = 0.75f;
    [Min(0f)] public float maxSpeed = 2f;
    [Range(0f, 1f)] public float stopProbability = 0.05f;
    [Min(0f)] public float minStopSeconds = 0.2f;
    [Min(0f)] public float maxStopSeconds = 0.8f;

    [Header("Scale randomization")]
    [Min(0.1f)] public float minScaleMultiplier = 0.8f;
    [Min(0.1f)] public float maxScaleMultiplier = 1.2f;

    Vector3 _startPosition;
    Vector3 _startScale;
    Vector3 _destination;
    Vector2 _previousDirection;
    float _speed;
    float _pauseRemaining;
    float _baseRoamRadius;
    float _baseMinSpeed;
    float _baseMaxSpeed;
    float _baseStopProbability;
    float _baseMinScaleMultiplier;
    float _baseMaxScaleMultiplier;
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
        _baseRoamRadius = roamRadius;
        _baseMinSpeed = minSpeed;
        _baseMaxSpeed = maxSpeed;
        _baseStopProbability = stopProbability;
        _baseMinScaleMultiplier = minScaleMultiplier;
        _baseMaxScaleMultiplier = maxScaleMultiplier;
        _initialized = true;
    }

    public void ApplyCurriculumDifficulty(int stage)
    {
        CaptureStartPose();
        float progress = Mathf.Clamp01(stage / 4f);
        roamRadius = Mathf.Lerp(5f, _baseRoamRadius, progress);
        minSpeed = Mathf.Lerp(0.5f, _baseMinSpeed, progress);
        maxSpeed = Mathf.Lerp(1.25f, _baseMaxSpeed, progress);
        stopProbability = Mathf.Lerp(0.1f, _baseStopProbability, progress);
        minScaleMultiplier = Mathf.Lerp(1f, _baseMinScaleMultiplier, progress);
        maxScaleMultiplier = Mathf.Lerp(1f, _baseMaxScaleMultiplier, progress);
    }

    public void ResetEpisode()
    {
        CaptureStartPose();
        transform.position = _startPosition;
        transform.localScale = _startScale * Random.Range(minScaleMultiplier, maxScaleMultiplier);
        _pauseRemaining = 0f;
        _previousDirection = Vector2.zero;
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

        Vector2 currentOffset = new Vector2(
            transform.position.x - _startPosition.x,
            transform.position.z - _startPosition.z);
        Vector2 offset = Vector2.zero;
        Vector2 direction = Vector2.zero;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Vector2 radialDirection = Random.insideUnitCircle;
            if (radialDirection.sqrMagnitude < 0.01f)
                radialDirection = Vector2.right;
            else
                radialDirection.Normalize();
            float radius = Random.Range(roamRadius * 0.45f, roamRadius);
            offset = radialDirection * radius;
            direction = offset - currentOffset;
            if (direction.sqrMagnitude > 2.25f &&
                (_previousDirection.sqrMagnitude < 0.01f ||
                 Vector2.Angle(_previousDirection, direction) >= 60f))
                break;
        }

        _destination = new Vector3(
            _startPosition.x + offset.x,
            _startPosition.y,
            _startPosition.z + offset.y);
        if (direction.sqrMagnitude > 0.01f)
            _previousDirection = direction.normalized;
        _speed = Random.Range(Mathf.Min(minSpeed, maxSpeed), Mathf.Max(minSpeed, maxSpeed));
    }
}
