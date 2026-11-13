using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lightweight detector surrogate. It projects target renderer bounds into the
/// camera image and applies detector-like jitter, dropouts and delivery latency.
/// It emits only four scalar values; no image is passed to the policy.
/// </summary>
public sealed class SimulatedBoundingBoxDetector : MonoBehaviour, IDroneTargetDetectionSource
{
    [Header("Projection")]
    public Camera observationCamera;
    public Transform target;
    [Min(1f)] public float sampleRateHz = 10f;

    [Header("Detector noise")]
    [Range(0f, 0.5f)] public float dropoutProbability = 0.05f;
    [Range(0f, 0.2f)] public float centerNoise = 0.015f;
    [Range(0f, 0.2f)] public float heightNoise = 0.015f;

    [Header("Pipeline timing")]
    [Min(0f)] public float minLatencySeconds = 0.10f;
    [Min(0f)] public float maxLatencySeconds = 0.20f;
    [Min(0.01f)] public float maxDetectionAgeSeconds = 0.40f;

    struct PendingDetection
    {
        public DroneTargetDetection Detection;
        public float DeliverAt;

        public PendingDetection(DroneTargetDetection detection, float deliverAt)
        {
            Detection = detection;
            DeliverAt = deliverAt;
        }
    }

    readonly Queue<PendingDetection> _pending = new Queue<PendingDetection>();
    DroneTargetDetection _current = new DroneTargetDetection();
    bool _hasCurrent;
    float _nextSampleTime;

    public DroneTargetDetection Current
    {
        get
        {
            if (!_hasCurrent || !_current.Visible ||
                Time.time - _current.CaptureTime > maxDetectionAgeSeconds)
                return DroneTargetDetection.None(Time.time);

            return _current;
        }
    }

    void Update()
    {
        if (observationCamera == null || target == null) return;

        float interval = 1f / Mathf.Max(1f, sampleRateHz);
        if (Time.time >= _nextSampleTime)
        {
            _nextSampleTime = Time.time + interval;
            DroneTargetDetection detection = ProjectTarget();

            if (detection.Visible)
            {
                if (Random.value < dropoutProbability)
                {
                    detection = DroneTargetDetection.None(Time.time);
                }
                else
                {
                    detection.CenterX = Mathf.Clamp(
                        detection.CenterX + Random.Range(-centerNoise, centerNoise), -1f, 1f);
                    detection.CenterY = Mathf.Clamp(
                        detection.CenterY + Random.Range(-centerNoise, centerNoise), -1f, 1f);
                    detection.Height = Mathf.Clamp01(
                        detection.Height + Random.Range(-heightNoise, heightNoise));
                }
            }

            float minDelay = Mathf.Min(minLatencySeconds, maxLatencySeconds);
            float maxDelay = Mathf.Max(minLatencySeconds, maxLatencySeconds);
            float delay = Random.Range(minDelay, maxDelay);
            _pending.Enqueue(new PendingDetection(detection, Time.time + delay));
        }

        while (_pending.Count > 0 && _pending.Peek().DeliverAt <= Time.time)
        {
            _current = _pending.Dequeue().Detection;
            _hasCurrent = true;
        }
    }

    DroneTargetDetection ProjectTarget()
    {
        if (observationCamera == null || target == null)
            return DroneTargetDetection.None(Time.time);

        Renderer[] renderers = target.GetComponentsInChildren<Renderer>();
        bool found = false;
        float minX = 1f, minY = 1f, maxX = 0f, maxY = 0f;

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null || !renderer.enabled) continue;
            Bounds bounds = renderer.bounds;

            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 world = new Vector3(
                    (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                Vector3 viewport = observationCamera.WorldToViewportPoint(world);
                if (viewport.z <= observationCamera.nearClipPlane) continue;

                found = true;
                minX = Mathf.Min(minX, viewport.x);
                maxX = Mathf.Max(maxX, viewport.x);
                minY = Mathf.Min(minY, viewport.y);
                maxY = Mathf.Max(maxY, viewport.y);
            }
        }

        if (!found || maxX <= 0f || minX >= 1f || maxY <= 0f || minY >= 1f)
            return DroneTargetDetection.None(Time.time);

        minX = Mathf.Clamp01(minX);
        maxX = Mathf.Clamp01(maxX);
        minY = Mathf.Clamp01(minY);
        maxY = Mathf.Clamp01(maxY);
        float width = maxX - minX;
        float height = maxY - minY;
        if (width <= 0f || height <= 0f)
            return DroneTargetDetection.None(Time.time);

        // Center x/y are [-1,1], with positive y upward; height is image fraction.
        float centerX = ((minX + maxX) * 0.5f) * 2f - 1f;
        float centerY = ((minY + maxY) * 0.5f) * 2f - 1f;
        return new DroneTargetDetection(centerX, centerY, height, true, Time.time);
    }

    public void ResetDetection()
    {
        _pending.Clear();
        _current = DroneTargetDetection.None(Time.time);
        _hasCurrent = true;
        _nextSampleTime = Time.time;
    }
}
