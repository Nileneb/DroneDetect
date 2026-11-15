using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Random = UnityEngine.Random;

/// <summary>
/// Runs MiDaS depth estimation and YOLO person detection on the drone camera
/// frame (simulated camera or decoded AR.Drone video), adapting their outputs
/// to DroneFollow's depth sensor and detection contract.
/// </summary>
public sealed class DroneFollowSentisPerception :
    MonoBehaviour,
    IDroneTargetDetectionSource,
    IDroneDetectionViewportSource
{
    const float PersonBoxIouThreshold = 0.45f;
    const int MaxPersonBoxesAfterNms = 20;

    struct DetectionCandidate
    {
        public float CenterX;
        public float CenterY;
        public float Width;
        public float Height;
        public float Score;
    }

    public enum DepthModelChoice
    {
        Small,
        Large
    }

    public enum InputFraming
    {
        FullFrameStretch,
        CenterCrop
    }

    public enum DepthNormalization
    {
        PerFramePercentile,
        Fixed
    }

    public enum FrameSource
    {
        ObservationCamera,
        External
    }

    struct DelayedDetection
    {
        public float ReleaseTime;
        public DroneTargetDetection Detection;
        public Rect ViewportBox;
    }

    [Header("Models")]
    public ModelAsset midasLargeModel;
    public ModelAsset midasSmallModel;
    public ModelAsset yoloModel;
    public DepthModelChoice depthModelChoice = DepthModelChoice.Small;
    [Min(32)] public int midasLargeInputSize = 384;
    [Min(32)] public int midasSmallInputSize = 256;
    [Min(32)] public int yoloInputSize = 640;

    [Header("Camera input")]
    // WHY(midas-quality, framing): the square center crop dropped ~44% of the 16:9 frame (people at the edges);
    // A/B on Agent1 showed stretching the full frame keeps those shapes and beats letterboxing, which wastes resolution.
    public InputFraming inputFraming = InputFraming.FullFrameStretch;
    [Tooltip("ObservationCamera renders the simulator; External expects SubmitFrame() calls, e.g. from the AR.Drone video decoder.")]
    public FrameSource frameSource = FrameSource.ObservationCamera;
    public Camera observationCamera;
    public RenderTexture rgbInput;
    public RenderTexture depthOutput;
    public RawImage depthPreview;
    [Min(1f)] public float depthInferenceRateHz = 15f;
    [Min(1f)] public float detectionInferenceRateHz = 10f;

    [Header("MiDaS output")]
    // WHY(midas-quality, scale): MiDaS emits scale-ambiguous relative inverse depth (small model ~60..1200,
    // large ~0..6000 on Agent1), so a fixed 0..4096 range collapsed the small model into near-white frames.
    public DepthNormalization depthNormalization = DepthNormalization.PerFramePercentile;
    [Range(0f, 0.5f)] public float lowDepthPercentile = 0.02f;
    [Range(0.5f, 1f)] public float highDepthPercentile = 0.98f;
    [Tooltip("Weight of the newest frame's percentile range; 1 = no temporal smoothing.")]
    [Range(0.01f, 1f)] public float depthRangeAdaptation = 0.3f;
    [Min(0f)] public float minimumDepthValue;
    [Min(0.01f)] public float maximumDepthValue = 4096f;
    public bool invertDepth = true;

    [Header("YOLO output")]
    [Range(0f, 1f)] public float personScoreThreshold = 0.2f;
    [Min(0.01f)] public float maxDetectionAgeSeconds = 0.4f;

    // WHY(sim-to-real, detection): on hardware the box arrives after Wi-Fi video + phone inference latency and
    // YOLO misses frames; training with the same imperfections keeps the policy from relying on a perfect detector.
    [Header("Training randomization (simulator only)")]
    public bool randomizeDetections;
    public Vector2 detectionLatencyRangeSeconds = new Vector2(0.05f, 0.25f);
    [Range(0f, 0.5f)] public float detectionDropoutProbability = 0.08f;
    [Range(0f, 0.2f)] public float detectionCenterJitter = 0.02f;
    [Range(0f, 0.5f)] public float detectionHeightJitterFraction = 0.05f;

    [Header("Target identity")]
    [Tooltip("Lock onto the most confident person automatically (simulator). Off on the phone: the operator taps the target.")]
    public bool autoLockTarget = true;
    [Tooltip("OSNet person re-identification model. Without it, colour signatures identify the target.")]
    public ModelAsset reidModel;

    Worker _depthWorker;
    Worker _yoloWorker;
    Tensor<float> _depthInput;
    Tensor<float> _yoloInput;
    Camera _camera;
    RenderTexture _depthInputTexture;
    RenderTexture _yoloInputTexture;
    Texture2D _depthOutputTexture;
    Color32[] _depthPixels;
    Texture2D _depthPreviewTexture;
    Color32[] _depthPreviewPixels;
    float[] _depthSamples;
    float _depthLow;
    float _depthHigh;
    bool _hasDepthRange;
    bool _dumpRequested;
    string _dumpDirectory;
    DroneTargetDetection _current = DroneTargetDetection.None();
    Rect _currentViewportBox;
    bool _hasCurrentViewportBox;
    bool _depthPending;
    bool _yoloPending;
    float _nextDepthInferenceTime;
    float _nextYoloInferenceTime;
    float _yoloCaptureTime;
    int _resetGeneration;
    float _episodeDetectionLatency;
    readonly Queue<DelayedDetection> _delayedDetections = new Queue<DelayedDetection>();

    public int DetectionFramesProcessed { get; private set; }
    public float LastPersonScore { get; private set; }
    public PersonTargetTracker Tracker { get; private set; } = new PersonTargetTracker();
    public int PeopleInView { get; private set; }

    const int AppearanceWidth = 160;
    const int AppearanceHeight = 90;
    const int ReidMaxCandidates = 3;
    const int ReidInputWidth = 128;
    const int ReidInputHeight = 256;
    RenderTexture _appearanceTexture;
    Color32[] _appearancePixels;
    bool _appearancePending;
    Worker _reidWorker;
    Tensor<float> _reidInput;
    RenderTexture _reidInputTexture;
    bool _identityPending;
    readonly List<PersonTargetTracker.Candidate> _trackerCandidates = new List<PersonTargetTracker.Candidate>();

    public DroneTargetDetection Current
    {
        get
        {
            ReleaseDelayedDetections();
            if (!_current.Visible ||
                Time.time - _current.CaptureTime > maxDetectionAgeSeconds)
                return DroneTargetDetection.None(Time.time);

            return _current;
        }
    }

    void OnEnable()
    {
        if (observationCamera == null)
            observationCamera = GetComponent<Camera>();
        _camera = observationCamera;

        ModelAsset depthAsset = depthModelChoice == DepthModelChoice.Small
            ? midasSmallModel
            : midasLargeModel;
        int depthInputSize = depthModelChoice == DepthModelChoice.Small
            ? midasSmallInputSize
            : midasLargeInputSize;

        if (depthNormalization == DepthNormalization.Fixed && maximumDepthValue <= minimumDepthValue)
            Debug.LogError("[DroneFollowSentisPerception] Maximum depth value must exceed minimum depth value.", this);

        if (depthAsset == null)
            Debug.LogError("[DroneFollowSentisPerception] Assign a MiDaS model to generate the Agent's depth observation.", this);
        else
        {
            Model depthModel = ModelLoader.Load(depthAsset);
            depthInputSize = ResolveInputSize(depthModel, depthInputSize, "MiDaS");
            _depthWorker = new Worker(depthModel, BackendType.GPUCompute);
            _depthInput = new Tensor<float>(new TensorShape(1, 3, depthInputSize, depthInputSize));
            _depthInputTexture = CreateInputTexture(depthInputSize, "DroneFollow_MiDaS_Input", RenderTextureFormat.ARGBFloat);
            _depthOutputTexture = new Texture2D(
                DroneFollowContract.DepthObservationWidth,
                DroneFollowContract.DepthObservationHeight,
                TextureFormat.RGB24,
                false,
                true)
            {
                name = "DroneFollow_MiDaS_Depth",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _depthPixels = new Color32[
                DroneFollowContract.DepthObservationWidth * DroneFollowContract.DepthObservationHeight];
        }

        if (yoloModel == null)
            Debug.LogError("[DroneFollowSentisPerception] Assign the YOLO model to provide target detections.", this);
        else
        {
            Model detectionModel = ModelLoader.Load(yoloModel);
            yoloInputSize = ResolveInputSize(detectionModel, yoloInputSize, "YOLO");
            _yoloWorker = new Worker(detectionModel, BackendType.GPUCompute);
            _yoloInput = new Tensor<float>(new TensorShape(1, 3, yoloInputSize, yoloInputSize));
            _yoloInputTexture = CreateInputTexture(yoloInputSize, "DroneFollow_YOLO_Input", RenderTextureFormat.ARGB32);
        }

        if (reidModel != null)
        {
            _reidWorker = new Worker(ModelLoader.Load(reidModel), BackendType.GPUCompute);
            _reidInput = new Tensor<float>(new TensorShape(1, 3, ReidInputHeight, ReidInputWidth));
            _reidInputTexture = new RenderTexture(ReidInputWidth, ReidInputHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "DroneFollow_ReID_Input"
            };
            _reidInputTexture.Create();
            Tracker = new PersonTargetTracker(PersonTargetTracker.AppearanceModel.ReidEmbedding);
        }
        else
        {
            _appearanceTexture = new RenderTexture(AppearanceWidth, AppearanceHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "DroneFollow_Appearance"
            };
            _appearanceTexture.Create();
            Tracker = new PersonTargetTracker(PersonTargetTracker.AppearanceModel.ColourHistogram);
        }


        if (rgbInput == null)
        {
            Debug.LogError("[DroneFollowSentisPerception] Assign the RGB input RenderTexture.", this);
            return;
        }

        if (frameSource == FrameSource.External)
            return;

        if (observationCamera == null || observationCamera.targetTexture != rgbInput)
            Debug.LogError("[DroneFollowSentisPerception] The observation camera must render to the assigned RGB RenderTexture.", this);
        else if (Mathf.Abs(observationCamera.fieldOfView - DroneFollowContract.CameraVerticalFovDegrees) > 0.5f)
            Debug.LogError(
                $"[DroneFollowSentisPerception] Observation camera FOV is {observationCamera.fieldOfView:F1}°, the AR.Drone camera has {DroneFollowContract.CameraVerticalFovDegrees}°. Re-run DroneDetect > Configure Active Scene for DroneFollow.",
                this);

        if (GraphicsSettings.currentRenderPipeline == null)
            Camera.onPostRender += OnCameraPostRender;
        else
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
    }

    /// <summary>Feeds one externally decoded camera frame (External frame source).</summary>
    public void SubmitFrame(Texture frame)
    {
        if (frameSource != FrameSource.External)
            throw new System.InvalidOperationException("[DroneFollowSentisPerception] SubmitFrame requires the External frame source.");
        if (frame == null || rgbInput == null)
            return;

        Graphics.Blit(frame, rgbInput);
        ProcessCameraFrame();
    }

    void OnDisable()
    {
        Camera.onPostRender -= OnCameraPostRender;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        _resetGeneration++;
        _depthWorker?.Dispose();
        _depthWorker = null;
        _yoloWorker?.Dispose();
        _yoloWorker = null;
        _reidWorker?.Dispose();
        _reidWorker = null;
        _reidInput?.Dispose();
        _reidInput = null;
        ReleaseInputTexture(ref _reidInputTexture);
        ReleaseInputTexture(ref _appearanceTexture);
        _appearancePixels = null;
        _appearancePending = false;
        _identityPending = false;
        _depthInput?.Dispose();
        _depthInput = null;
        _yoloInput?.Dispose();
        _yoloInput = null;
        ReleaseInputTexture(ref _depthInputTexture);
        ReleaseInputTexture(ref _yoloInputTexture);
        if (_depthOutputTexture != null)
        {
            Destroy(_depthOutputTexture);
            _depthOutputTexture = null;
        }
        if (_depthPreviewTexture != null)
        {
            Destroy(_depthPreviewTexture);
            _depthPreviewTexture = null;
        }
        _depthPixels = null;
        _depthPreviewPixels = null;
        _depthSamples = null;
        _hasDepthRange = false;
        _depthPending = false;
        _yoloPending = false;
        _delayedDetections.Clear();
    }

    void OnCameraPostRender(Camera renderedCamera)
    {
        if (renderedCamera == _camera)
            ProcessCameraFrame();
    }

    void OnEndCameraRendering(ScriptableRenderContext context, Camera renderedCamera)
    {
        if (renderedCamera == _camera)
            ProcessCameraFrame();
    }

    void ProcessCameraFrame()
    {
        if (rgbInput == null)
            return;

        Rect crop = GetInputCrop(rgbInput);
        if (_depthWorker != null && !_depthPending && Time.time >= _nextDepthInferenceTime)
        {
            _nextDepthInferenceTime = Time.time + 1f / Mathf.Max(1f, depthInferenceRateHz);
            PrepareInput(rgbInput, _depthInputTexture, crop);
            TextureConverter.ToTensor(_depthInputTexture, _depthInput, new TextureTransform());
            bool dumpThisFrame = _dumpRequested;
            if (dumpThisFrame)
                DumpModelInput();
            _depthWorker.Schedule(_depthInput);
            if (_depthWorker.PeekOutput() is not Tensor<float> output)
                throw new System.InvalidOperationException("[DroneFollowSentisPerception] MiDaS must produce a float tensor.");

            _depthPending = true;
            int generation = _resetGeneration;
            float frameAspect = rgbInput.width * crop.width / (rgbInput.height * crop.height);
            var readback = output.ReadbackAndCloneAsync();
            var awaiter = readback.GetAwaiter();
            awaiter.OnCompleted(() =>
            {
                _depthPending = false;
                using Tensor<float> readable = awaiter.GetResult();
                CompleteDepthReadback(readable, generation, frameAspect);
                if (dumpThisFrame)
                    DumpDepthResult(readable);
            });
        }

        if (_yoloWorker != null && !_yoloPending && !_identityPending && Time.time >= _nextYoloInferenceTime)
        {
            _nextYoloInferenceTime = Time.time + 1f / Mathf.Max(1f, detectionInferenceRateHz);
            PrepareInput(rgbInput, _yoloInputTexture, crop);
            if (_reidWorker == null)
                RequestAppearanceFrame(crop);
            TextureConverter.ToTensor(_yoloInputTexture, _yoloInput, new TextureTransform());
            _yoloWorker.Schedule(_yoloInput);
            if (_yoloWorker.PeekOutput() is not Tensor<float> output)
                throw new System.InvalidOperationException("[DroneFollowSentisPerception] YOLO must produce a float tensor.");

            _yoloPending = true;
            _yoloCaptureTime = Time.time;
            int generation = _resetGeneration;
            var readback = output.ReadbackAndCloneAsync();
            var awaiter = readback.GetAwaiter();
            awaiter.OnCompleted(() =>
            {
                _yoloPending = false;
                using Tensor<float> readable = awaiter.GetResult();
                CompleteDetectionReadback(readable, generation, crop);
            });
        }
    }

    // A small CPU copy of the YOLO frame gives each person box a colour signature (used without OSNet).
    void RequestAppearanceFrame(Rect crop)
    {
        if (_appearancePending)
            return;
        _appearancePending = true;
        PrepareInput(rgbInput, _appearanceTexture, crop);
        AsyncGPUReadback.Request(_appearanceTexture, 0, TextureFormat.RGBA32, request =>
        {
            _appearancePending = false;
            if (this == null)
                return;
            if (request.hasError)
            {
                Debug.LogWarning("[DroneFollowSentisPerception] Appearance readback failed; target identity uses position only this frame.");
                return;
            }
            _appearancePixels ??= new Color32[AppearanceWidth * AppearanceHeight];
            request.GetData<Color32>().CopyTo(_appearancePixels);
        });
    }

    /// <summary>Operator tap: lock onto the person nearest to this viewport point (0..1, origin bottom-left).</summary>
    public void RequestTargetLock(Vector2 viewportPoint)
    {
        Tracker.RequestLock(new Vector2(viewportPoint.x, 1f - viewportPoint.y));
    }

    void PrepareInput(RenderTexture source, RenderTexture target, Rect crop)
    {
        // WHY(midas-quality, blit): the custom crop shader never received _MainTex and fed both models flat 0.5 gray;
        // the built-in scale/offset Blit does the same crop without a material.
        Graphics.Blit(source, target, CropScale(crop), CropOffset(crop));
    }

    void CompleteDepthReadback(Tensor<float> readable, int generation, float frameAspect)
    {
        if (this == null || generation != _resetGeneration)
            return;

        TensorShape shape = readable.shape;
        if (shape.rank < 2 || shape[shape.rank - 1] <= 0 || shape[shape.rank - 2] <= 0)
            throw new System.InvalidOperationException($"[DroneFollowSentisPerception] Invalid MiDaS output shape: {shape}.");

        int width = shape[shape.rank - 1];
        int height = shape[shape.rank - 2];
        float[] values = readable.DownloadToArray();
        if (values.Length != width * height)
            throw new System.InvalidOperationException(
                $"[DroneFollowSentisPerception] Expected one depth channel but received {values.Length} values for {width}x{height}.");

        UpdateDepthRange(values);

        // Tensor row 0 is the image top (TextureTransform default CoordOrigin.TopLeft); Texture2D row 0 is the bottom.
        int targetWidth = DroneFollowContract.DepthObservationWidth;
        int targetHeight = DroneFollowContract.DepthObservationHeight;
        for (int y = 0; y < targetHeight; y++)
        {
            int sourceY0 = y * height / targetHeight;
            int sourceY1 = Mathf.Max(sourceY0 + 1, (y + 1) * height / targetHeight);
            for (int x = 0; x < targetWidth; x++)
            {
                int sourceX0 = x * width / targetWidth;
                int sourceX1 = Mathf.Max(sourceX0 + 1, (x + 1) * width / targetWidth);
                float sum = 0f;
                int count = 0;
                for (int sy = sourceY0; sy < sourceY1; sy++)
                for (int sx = sourceX0; sx < sourceX1; sx++)
                {
                    float value = values[sy * width + sx];
                    if (!IsFinite(value))
                        continue;
                    sum += value;
                    count++;
                }

                byte gray = count == 0 ? (byte)0 : NormalizeDepth(sum / count);
                _depthPixels[(targetHeight - 1 - y) * targetWidth + x] = new Color32(gray, gray, gray, 255);
            }
        }

        if (depthPreview != null)
            UpdateDepthPreview(values, width, height, frameAspect);

        _depthOutputTexture.SetPixels32(_depthPixels);
        _depthOutputTexture.Apply(false, false);
        if (depthOutput != null)
            Graphics.Blit(_depthOutputTexture, depthOutput);
    }

    [ContextMenu("Dump Next Perception Frame")]
    void RequestPerceptionDump()
    {
        if (!Application.isPlaying)
        {
            Debug.LogError("[DroneFollowSentisPerception] Enter Play mode before dumping a perception frame.", this);
            return;
        }

        _dumpRequested = true;
    }

    void DumpModelInput()
    {
        _dumpRequested = false;
        _dumpDirectory = System.IO.Path.Combine(
            Application.dataPath, "..", "Logs", "PerceptionDebug", System.DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        System.IO.Directory.CreateDirectory(_dumpDirectory);
        SaveRenderTexture(rgbInput, "1_rgb_input.png");
        SaveRenderTexture(_depthInputTexture, "2_midas_model_input.png");

        using Tensor<float> input = _depthInput.ReadbackAndClone();
        float[] values = input.DownloadToArray();
        int plane = values.Length / 3;
        var report = new System.Text.StringBuilder();
        report.AppendLine($"rgbInput {rgbInput.width}x{rgbInput.height} format={rgbInput.format} created={rgbInput.IsCreated()} cameraTarget={(observationCamera != null ? observationCamera.targetTexture?.name : "none")}");
        report.AppendLine($"framing={inputFraming} crop={GetInputCrop(rgbInput)} midasInput={_depthInput.shape}");
        for (int channel = 0; channel < 3; channel++)
        {
            float min = float.MaxValue, max = float.MinValue, sum = 0f;
            for (int i = channel * plane; i < (channel + 1) * plane; i++)
            {
                min = Mathf.Min(min, values[i]);
                max = Mathf.Max(max, values[i]);
                sum += values[i];
            }
            report.AppendLine($"input channel {channel}: min={min:F4} max={max:F4} mean={sum / plane:F4}");
        }

        System.IO.File.WriteAllText(System.IO.Path.Combine(_dumpDirectory, "report.txt"), report.ToString());
    }

    void DumpDepthResult(Tensor<float> readable)
    {
        float[] values = readable.DownloadToArray();
        float min = float.MaxValue, max = float.MinValue;
        foreach (float value in values)
        {
            min = Mathf.Min(min, value);
            max = Mathf.Max(max, value);
        }

        System.IO.File.AppendAllText(
            System.IO.Path.Combine(_dumpDirectory, "report.txt"),
            $"midas output {readable.shape}: min={min:F3} max={max:F3} normalizedRange=[{_depthLow:F3}, {_depthHigh:F3}] invert={invertDepth}\n");
        if (_depthPreviewTexture != null)
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(_dumpDirectory, "3_depth_preview.png"), _depthPreviewTexture.EncodeToPNG());
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(_dumpDirectory, "4_depth_observation_84.png"), _depthOutputTexture.EncodeToPNG());
        Debug.Log($"[DroneFollowSentisPerception] Perception frame dumped to {System.IO.Path.GetFullPath(_dumpDirectory)}", this);
    }

    void SaveRenderTexture(RenderTexture source, string fileName)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(source, temporary);
        RenderTexture.active = temporary;
        var texture = new Texture2D(source.width, source.height, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
        texture.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(temporary);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(_dumpDirectory, fileName), texture.EncodeToPNG());
        Destroy(texture);
    }

    void UpdateDepthRange(float[] values)
    {
        float low;
        float high;
        if (depthNormalization == DepthNormalization.Fixed)
        {
            low = minimumDepthValue;
            high = maximumDepthValue;
        }
        else
        {
            const int maxSamples = 16384;
            int stride = Mathf.Max(1, values.Length / maxSamples);
            int sampleCount = 0;
            if (_depthSamples == null || _depthSamples.Length < values.Length / stride + 1)
                _depthSamples = new float[values.Length / stride + 1];
            for (int i = 0; i < values.Length; i += stride)
            {
                if (IsFinite(values[i]))
                    _depthSamples[sampleCount++] = values[i];
            }

            if (sampleCount == 0)
                throw new System.InvalidOperationException("[DroneFollowSentisPerception] MiDaS produced no finite depth values.");

            System.Array.Sort(_depthSamples, 0, sampleCount);
            low = _depthSamples[Mathf.Clamp(Mathf.RoundToInt(lowDepthPercentile * (sampleCount - 1)), 0, sampleCount - 1)];
            high = _depthSamples[Mathf.Clamp(Mathf.RoundToInt(highDepthPercentile * (sampleCount - 1)), 0, sampleCount - 1)];
            if (high - low < 1e-3f)
                high = low + 1e-3f;

            if (_hasDepthRange)
            {
                low = Mathf.Lerp(_depthLow, low, depthRangeAdaptation);
                high = Mathf.Lerp(_depthHigh, high, depthRangeAdaptation);
            }
        }

        _depthLow = low;
        _depthHigh = high;
        _hasDepthRange = true;
    }

    byte NormalizeDepth(float depth)
    {
        float normalized = Mathf.InverseLerp(_depthLow, _depthHigh, depth);
        if (invertDepth)
            normalized = 1f - normalized;
        return (byte)Mathf.RoundToInt(Mathf.Clamp01(normalized) * 255f);
    }

    void UpdateDepthPreview(float[] values, int width, int height, float frameAspect)
    {
        // The model sees a square input; restoring the camera aspect on screen undoes the stretch.
        AspectRatioFitter fitter = depthPreview.GetComponent<AspectRatioFitter>();
        if (fitter == null)
            fitter = depthPreview.gameObject.AddComponent<AspectRatioFitter>();
        fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        fitter.aspectRatio = frameAspect;

        if (_depthPreviewTexture == null ||
            _depthPreviewTexture.width != width ||
            _depthPreviewTexture.height != height)
        {
            if (_depthPreviewTexture != null)
                Destroy(_depthPreviewTexture);
            _depthPreviewTexture = new Texture2D(width, height, TextureFormat.RGB24, false, true)
            {
                name = "DroneFollow_MiDaS_Preview",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _depthPreviewPixels = new Color32[width * height];
            depthPreview.texture = _depthPreviewTexture;
        }

        for (int y = 0; y < height; y++)
        {
            int textureRow = (height - 1 - y) * width;
            int sourceRow = y * width;
            for (int x = 0; x < width; x++)
            {
                float value = values[sourceRow + x];
                byte gray = IsFinite(value) ? NormalizeDepth(value) : (byte)0;
                _depthPreviewPixels[textureRow + x] = new Color32(gray, gray, gray, 255);
            }
        }

        _depthPreviewTexture.SetPixels32(_depthPreviewPixels);
        _depthPreviewTexture.Apply(false, false);
    }

    void CompleteDetectionReadback(Tensor<float> readable, int generation, Rect crop)
    {
        if (this == null || generation != _resetGeneration)
            return;

        TensorShape shape = readable.shape;
        if (shape.rank != 3 || shape[0] != 1)
            throw new System.InvalidOperationException($"[DroneFollowSentisPerception] Expected a batch-one rank-three YOLO output, received {shape}.");

        bool channelsFirst;
        if (shape[1] <= 256 && shape[2] > shape[1])
            channelsFirst = true;
        else if (shape[2] <= 256 && shape[1] > shape[2])
            channelsFirst = false;
        else
            throw new System.InvalidOperationException($"[DroneFollowSentisPerception] Could not identify the class and candidate axes in YOLO output {shape}.");

        int channelCount = channelsFirst ? shape[1] : shape[2];
        int candidateCount = channelsFirst ? shape[2] : shape[1];
        if (channelCount < 5)
            throw new System.InvalidOperationException($"[DroneFollowSentisPerception] YOLO output must have at least four box channels and one class channel, received {shape}.");

        float[] values = readable.DownloadToArray();
        if (values.Length != channelCount * candidateCount)
            throw new System.InvalidOperationException("[DroneFollowSentisPerception] YOLO output data does not match its shape.");

        const int personClassId = 0;
        if (channelCount <= 4 + personClassId)
            throw new System.InvalidOperationException("[DroneFollowSentisPerception] YOLO output does not contain the COCO person class.");

        float GetValue(int channel, int candidate)
        {
            return channelsFirst
                ? values[channel * candidateCount + candidate]
                : values[candidate * channelCount + channel];
        }

        var candidates = new List<DetectionCandidate>();
        float bestScore = 0f;
        for (int candidate = 0; candidate < candidateCount; candidate++)
        {
            float score = GetValue(4 + personClassId, candidate);
            if (score > bestScore)
                bestScore = score;

            if (float.IsNaN(score) || float.IsInfinity(score) || score < personScoreThreshold)
                continue;

            float centerX = GetValue(0, candidate) / yoloInputSize;
            float centerY = GetValue(1, candidate) / yoloInputSize;
            float width = GetValue(2, candidate) / yoloInputSize;
            float height = GetValue(3, candidate) / yoloInputSize;
            if (!IsFinite(centerX) || !IsFinite(centerY) ||
                !IsFinite(width) || !IsFinite(height) || width <= 0f || height <= 0f)
                continue;

            float minX = Mathf.Clamp01(centerX - width * 0.5f);
            float maxX = Mathf.Clamp01(centerX + width * 0.5f);
            float minY = Mathf.Clamp01(centerY - height * 0.5f);
            float maxY = Mathf.Clamp01(centerY + height * 0.5f);
            if (maxX <= minX || maxY <= minY)
                continue;

            candidates.Add(new DetectionCandidate
            {
                CenterX = (minX + maxX) * 0.5f,
                CenterY = (minY + maxY) * 0.5f,
                Width = maxX - minX,
                Height = maxY - minY,
                Score = score
            });
        }

        DetectionFramesProcessed++;
        LastPersonScore = bestScore;
        if (candidates.Count == 0 ||
            (randomizeDetections && Random.value < detectionDropoutProbability))
        {
            PublishDetection(DroneTargetDetection.None(_yoloCaptureTime), Rect.zero);
            return;
        }

        candidates.Sort((left, right) => right.Score.CompareTo(left.Score));
        var kept = new List<DetectionCandidate>(MaxPersonBoxesAfterNms);
        foreach (DetectionCandidate candidate in candidates)
        {
            bool overlapsKeptBox = false;
            foreach (DetectionCandidate keptCandidate in kept)
            {
                if (CalculateIoU(candidate, keptCandidate) > PersonBoxIouThreshold)
                {
                    overlapsKeptBox = true;
                    break;
                }
            }

            if (overlapsKeptBox)
                continue;

            kept.Add(candidate);
            if (kept.Count == MaxPersonBoxesAfterNms)
                break;
        }

        PeopleInView = kept.Count;
        _trackerCandidates.Clear();
        int identityCandidates = _reidWorker != null ? Mathf.Min(kept.Count, ReidMaxCandidates) : kept.Count;
        for (int i = 0; i < identityCandidates; i++)
        {
            DetectionCandidate candidate = kept[i];
            var box = new Rect(
                candidate.CenterX - candidate.Width * 0.5f,
                candidate.CenterY - candidate.Height * 0.5f,
                candidate.Width,
                candidate.Height);
            _trackerCandidates.Add(new PersonTargetTracker.Candidate
            {
                Box = box,
                Score = candidate.Score,
                Signature = _reidWorker == null && _appearancePixels != null
                    ? AppearanceSignature.Compute(_appearancePixels, AppearanceWidth, AppearanceHeight, box)
                    : null
            });
        }

        float captureTime = _yoloCaptureTime;
        if (_reidWorker == null)
        {
            ResolveTarget(kept, crop, captureTime);
            return;
        }

        _identityPending = true;
        EmbedCandidate(0, kept, crop, captureTime, generation);
    }

    /// <summary>Runs OSNet on one person crop after another, then resolves the target identity.</summary>
    void EmbedCandidate(int index, List<DetectionCandidate> kept, Rect crop, float captureTime, int generation)
    {
        if (this == null || generation != _resetGeneration)
        {
            _identityPending = false;
            return;
        }

        if (index >= _trackerCandidates.Count)
        {
            _identityPending = false;
            ResolveTarget(kept, crop, captureTime);
            return;
        }

        Rect box = _trackerCandidates[index].Box;
        // Box is top-left normalized inside the crop; Blit offsets are bottom-left UVs of the full texture.
        var scale = new Vector2(box.width * crop.width, box.height * crop.height);
        var offset = new Vector2(crop.x + box.x * crop.width, crop.y + (1f - box.yMax) * crop.height);
        Graphics.Blit(rgbInput, _reidInputTexture, scale, offset);
        TextureConverter.ToTensor(_reidInputTexture, _reidInput, new TextureTransform());
        _reidWorker.Schedule(_reidInput);
        if (_reidWorker.PeekOutput() is not Tensor<float> output)
            throw new System.InvalidOperationException("[DroneFollowSentisPerception] The re-identification model must produce a float tensor.");

        var awaiter = output.ReadbackAndCloneAsync().GetAwaiter();
        awaiter.OnCompleted(() =>
        {
            using Tensor<float> embedding = awaiter.GetResult();
            if (this == null || generation != _resetGeneration)
            {
                _identityPending = false;
                return;
            }
            PersonTargetTracker.Candidate candidate = _trackerCandidates[index];
            candidate.Signature = embedding.DownloadToArray();
            _trackerCandidates[index] = candidate;
            EmbedCandidate(index + 1, kept, crop, captureTime, generation);
        });
    }

    void ResolveTarget(List<DetectionCandidate> kept, Rect crop, float captureTime)
    {
        int targetIndex = Tracker.Select(_trackerCandidates, Time.time, autoLockTarget);
        if (targetIndex < 0)
        {
            PublishDetection(DroneTargetDetection.None(captureTime), Rect.zero);
            return;
        }

        DetectionCandidate best = kept[targetIndex];
        if (randomizeDetections)
        {
            best.CenterX += Gaussian() * detectionCenterJitter * 0.5f;
            best.CenterY += Gaussian() * detectionCenterJitter * 0.5f;
            best.Height = Mathf.Clamp01(best.Height * (1f + Gaussian() * detectionHeightJitterFraction));
        }

        var detection = new DroneTargetDetection(
            best.CenterX * 2f - 1f,
            1f - best.CenterY * 2f,
            best.Height,
            true,
            captureTime);
        var viewportBox = new Rect(
            crop.x + (best.CenterX - best.Width * 0.5f) * crop.width,
            crop.y + (1f - best.CenterY - best.Height * 0.5f) * crop.height,
            best.Width * crop.width,
            best.Height * crop.height);
        PublishDetection(detection, viewportBox);
    }

    void PublishDetection(DroneTargetDetection detection, Rect viewportBox)
    {
        float latency = randomizeDetections ? _episodeDetectionLatency : 0f;
        _delayedDetections.Enqueue(new DelayedDetection
        {
            ReleaseTime = Time.time + latency,
            Detection = detection,
            ViewportBox = viewportBox
        });
        ReleaseDelayedDetections();
    }

    void ReleaseDelayedDetections()
    {
        while (_delayedDetections.Count > 0 && _delayedDetections.Peek().ReleaseTime <= Time.time)
        {
            DelayedDetection released = _delayedDetections.Dequeue();
            _current = released.Detection;
            _currentViewportBox = released.ViewportBox;
            _hasCurrentViewportBox = released.Detection.Visible;
        }
    }

    static float Gaussian()
    {
        float u1 = 1f - Random.value;
        float u2 = Random.value;
        return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
    }

    static float CalculateIoU(DetectionCandidate a, DetectionCandidate b)
    {
        float intersectionWidth = Mathf.Max(
            0f,
            Mathf.Min(a.CenterX + a.Width * 0.5f, b.CenterX + b.Width * 0.5f) -
            Mathf.Max(a.CenterX - a.Width * 0.5f, b.CenterX - b.Width * 0.5f));
        float intersectionHeight = Mathf.Max(
            0f,
            Mathf.Min(a.CenterY + a.Height * 0.5f, b.CenterY + b.Height * 0.5f) -
            Mathf.Max(a.CenterY - a.Height * 0.5f, b.CenterY - b.Height * 0.5f));
        float intersection = intersectionWidth * intersectionHeight;
        float union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union > 0f ? intersection / union : 0f;
    }

    static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

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

    public void ResetDetection()
    {
        if (autoLockTarget)
            Tracker.Reset();
        _resetGeneration++;
        _delayedDetections.Clear();
        _episodeDetectionLatency = Random.Range(detectionLatencyRangeSeconds.x, detectionLatencyRangeSeconds.y);
        _current = DroneTargetDetection.None(Time.time);
        _currentViewportBox = Rect.zero;
        _hasCurrentViewportBox = false;
    }

    static RenderTexture CreateInputTexture(int size, string textureName, RenderTextureFormat format)
    {
        var texture = new RenderTexture(size, size, 0, format, RenderTextureReadWrite.Linear)
        {
            name = textureName,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false
        };
        texture.Create();
        return texture;
    }

    static int ResolveInputSize(Model model, int configuredSize, string modelName)
    {
        if (model.inputs.Count != 1)
            throw new System.InvalidOperationException(
                $"[DroneFollowSentisPerception] {modelName} model must have exactly one image input; found {model.inputs.Count}.");

        DynamicTensorShape inputShape = model.inputs[0].shape;
        if (inputShape.IsStatic())
        {
            TensorShape shape = inputShape.ToTensorShape();
            if (shape.rank != 4 || shape[0] != 1 || shape[1] != 3 || shape[2] != shape[3])
                throw new System.InvalidOperationException(
                    $"[DroneFollowSentisPerception] {modelName} expects unsupported input shape {shape}; expected [1,3,size,size].");
            return shape[2];
        }

        return Mathf.Max(32, configuredSize);
    }

    static void ReleaseInputTexture(ref RenderTexture texture)
    {
        if (texture == null)
            return;

        texture.Release();
        Destroy(texture);
        texture = null;
    }

    static Vector2 CropScale(Rect crop) => new Vector2(crop.width, crop.height);
    static Vector2 CropOffset(Rect crop) => new Vector2(crop.x, crop.y);
    Rect GetInputCrop(RenderTexture source)
    {
        if (inputFraming == InputFraming.FullFrameStretch)
            return new Rect(0f, 0f, 1f, 1f);

        float aspect = source.width / (float)source.height;
        float width = aspect > 1f ? 1f / aspect : 1f;
        float height = aspect < 1f ? aspect : 1f;
        return new Rect((1f - width) * 0.5f, (1f - height) * 0.5f, width, height);
    }

}
