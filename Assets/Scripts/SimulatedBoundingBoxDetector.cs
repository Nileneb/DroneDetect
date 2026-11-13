using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lightweight detector surrogate. It projects target renderer bounds into the
/// camera image and applies detector-like jitter, dropouts and delivery latency.
/// It emits only four scalar values to the policy; no image is passed to it.
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
        public Rect ViewportBox;
        public Rect CropRect;
        public float CropBoxWidth;
        public bool HasViewportBox;
        public float DeliverAt;

        public PendingDetection(
            DroneTargetDetection detection,
            Rect viewportBox,
            Rect cropRect,
            float cropBoxWidth,
            bool hasViewportBox,
            float deliverAt)
        {
            Detection = detection;
            ViewportBox = viewportBox;
            CropRect = cropRect;
            CropBoxWidth = cropBoxWidth;
            HasViewportBox = hasViewportBox;
            DeliverAt = deliverAt;
        }
    }

    readonly Queue<PendingDetection> _pending = new Queue<PendingDetection>();
    DroneTargetDetection _current = new DroneTargetDetection();
    Rect _currentViewportBox;
    bool _hasCurrent;
    bool _hasCurrentViewportBox;
    float _nextSampleTime;
    float _baseDropoutProbability;
    float _baseCenterNoise;
    float _baseHeightNoise;
    float _baseMinLatencySeconds;
    float _baseMaxLatencySeconds;
    bool _curriculumBaseCaptured;

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

    public void ApplyCurriculumDifficulty(int stage)
    {
        if (!_curriculumBaseCaptured)
        {
            _baseDropoutProbability = dropoutProbability;
            _baseCenterNoise = centerNoise;
            _baseHeightNoise = heightNoise;
            _baseMinLatencySeconds = minLatencySeconds;
            _baseMaxLatencySeconds = maxLatencySeconds;
            _curriculumBaseCaptured = true;
        }

        float progress = Mathf.Clamp01(stage / 4f);
        dropoutProbability = _baseDropoutProbability * progress;
        centerNoise = _baseCenterNoise * progress;
        heightNoise = _baseHeightNoise * progress;
        minLatencySeconds = Mathf.Lerp(_baseMinLatencySeconds * 0.5f, _baseMinLatencySeconds, progress);
        maxLatencySeconds = Mathf.Lerp(_baseMaxLatencySeconds * 0.5f, _baseMaxLatencySeconds, progress);
    }

    /// <summary>
    /// Returns the normalized [0,1] viewport rectangle for the most recently
    /// delivered detection. This is debug visualization only and is not added
    /// to the policy observation vector.
    /// </summary>
    public bool TryGetCurrentViewportBox(out Rect viewportBox)
    {
        if (Current.Visible && _hasCurrentViewportBox)
        {
            viewportBox = _currentViewportBox;
            return true;
        }

        viewportBox = Rect.zero;
        return false;
    }

    void Update()
    {
        if (observationCamera == null || target == null) return;

        float interval = 1f / Mathf.Max(1f, sampleRateHz);
        if (Time.time >= _nextSampleTime)
        {
            _nextSampleTime = Time.time + interval;
            Rect viewportBox;
            Rect cropRect;
            float cropBoxWidth;
            DroneTargetDetection detection = ProjectTarget(out viewportBox, out cropRect, out cropBoxWidth);
            bool hasViewportBox = detection.Visible;

            if (detection.Visible)
            {
                if (Random.value < dropoutProbability)
                {
                    detection = DroneTargetDetection.None(Time.time);
                    viewportBox = Rect.zero;
                    hasViewportBox = false;
                }
                else
                {
                    detection.CenterX = Mathf.Clamp(
                        detection.CenterX + Random.Range(-centerNoise, centerNoise), -1f, 1f);
                    detection.CenterY = Mathf.Clamp(
                        detection.CenterY + Random.Range(-centerNoise, centerNoise), -1f, 1f);
                    detection.Height = Mathf.Clamp01(
                        detection.Height + Random.Range(-heightNoise, heightNoise));

                    // Keep the displayed box aligned with the noisy center
                    // and height in the centered depth-observation crop.
                    float centerX = (detection.CenterX + 1f) * 0.5f;
                    float centerY = (detection.CenterY + 1f) * 0.5f;
                    float x = Mathf.Clamp(centerX - cropBoxWidth * 0.5f, 0f, 1f - cropBoxWidth);
                    float y = Mathf.Clamp(centerY - detection.Height * 0.5f, 0f, 1f - detection.Height);
                    Rect cropBox = new Rect(x, y, cropBoxWidth, detection.Height);
                    viewportBox = new Rect(
                        cropRect.x + cropBox.x * cropRect.width,
                        cropRect.y + cropBox.y * cropRect.height,
                        cropBox.width * cropRect.width,
                        cropBox.height * cropRect.height);
                }
            }

            float minDelay = Mathf.Min(minLatencySeconds, maxLatencySeconds);
            float maxDelay = Mathf.Max(minLatencySeconds, maxLatencySeconds);
            float delay = Random.Range(minDelay, maxDelay);
            _pending.Enqueue(new PendingDetection(
                detection, viewportBox, cropRect, cropBoxWidth, hasViewportBox, Time.time + delay));
        }

        while (_pending.Count > 0 && _pending.Peek().DeliverAt <= Time.time)
        {
            PendingDetection delivered = _pending.Dequeue();
            _current = delivered.Detection;
            _currentViewportBox = delivered.ViewportBox;
            _hasCurrentViewportBox = delivered.HasViewportBox && delivered.Detection.Visible;
            _hasCurrent = true;
        }
    }

    DroneTargetDetection ProjectTarget(out Rect viewportBox, out Rect cropRect, out float cropBoxWidth)
    {
        viewportBox = Rect.zero;
        cropRect = Rect.zero;
        cropBoxWidth = 0f;
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

        float aspect = Mathf.Max(0.01f, observationCamera.aspect);
        float cropWidth = aspect > 1f ? 1f / aspect : 1f;
        float cropHeight = aspect < 1f ? aspect : 1f;
        cropRect = new Rect((1f - cropWidth) * 0.5f, (1f - cropHeight) * 0.5f, cropWidth, cropHeight);

        float clippedMinX = Mathf.Max(minX, cropRect.xMin);
        float clippedMaxX = Mathf.Min(maxX, cropRect.xMax);
        float clippedMinY = Mathf.Max(minY, cropRect.yMin);
        float clippedMaxY = Mathf.Min(maxY, cropRect.yMax);
        if (clippedMaxX <= clippedMinX || clippedMaxY <= clippedMinY)
            return DroneTargetDetection.None(Time.time);

        float cropMinX = Mathf.Clamp01((clippedMinX - cropRect.xMin) / cropRect.width);
        float cropMaxX = Mathf.Clamp01((clippedMaxX - cropRect.xMin) / cropRect.width);
        float cropMinY = Mathf.Clamp01((clippedMinY - cropRect.yMin) / cropRect.height);
        float cropMaxY = Mathf.Clamp01((clippedMaxY - cropRect.yMin) / cropRect.height);
        viewportBox = Rect.MinMaxRect(clippedMinX, clippedMinY, clippedMaxX, clippedMaxY);

        // Center x/y are [-1,1], with positive y upward; height is image fraction.
        float centerX = ((cropMinX + cropMaxX) * 0.5f) * 2f - 1f;
        float centerY = ((cropMinY + cropMaxY) * 0.5f) * 2f - 1f;
        cropBoxWidth = cropMaxX - cropMinX;
        float cropBoxHeight = cropMaxY - cropMinY;
        return new DroneTargetDetection(centerX, centerY, cropBoxHeight, true, Time.time);
    }

    public void ResetDetection()
    {
        _pending.Clear();
        _current = DroneTargetDetection.None(Time.time);
        _currentViewportBox = Rect.zero;
        _hasCurrentViewportBox = false;
        _hasCurrent = true;
        _nextSampleTime = Time.time;
    }
}
