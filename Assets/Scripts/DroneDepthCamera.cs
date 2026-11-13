using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Generates a centered grayscale depth view from the main camera render. The
/// output can be shared by the UI preview and an ML-Agents RenderTextureSensor.
/// </summary>
[RequireComponent(typeof(Camera))]
public class DroneDepthCamera : MonoBehaviour
{
    [Header("Depth Output")]
    public RawImage depthPreview;
    public RenderTexture outputTexture;
    [Min(32)] public int resolution = 256;
    [Min(0.1f)] public float maxVisualizationDistance = 20f;
    [Min(0)] public int updateIntervalSteps = 5;

    public RenderTexture DepthRT { get; private set; }

    Camera _cam;
    Material _depthMat;
    bool _ownsDepthRT;
    int _stepCounter;

    void Awake()
    {
        _cam = GetComponent<Camera>();
        _cam.depthTextureMode = DepthTextureMode.Depth;

        DepthRT = outputTexture;
        if (DepthRT == null)
        {
            int size = Mathf.Max(32, resolution);
            DepthRT = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32)
            {
                name = "DroneDepthRT"
            };
            _ownsDepthRT = true;
        }
        if (!DepthRT.IsCreated())
            DepthRT.Create();

        Shader shader = Shader.Find("DroneDetect/DepthVisualize");
        if (shader == null)
            Debug.LogError("[DroneDepthCamera] DepthVisualize shader not found. Check Assets/Shaders/.");
        else
        {
            _depthMat = new Material(shader);
            _depthMat.SetFloat("_MaxDepth", maxVisualizationDistance);
        }

        if (depthPreview != null)
            depthPreview.texture = DepthRT;
    }

    void OnDestroy()
    {
        if (_ownsDepthRT && DepthRT != null)
        {
            DepthRT.Release();
            Destroy(DepthRT);
        }
        if (_depthMat != null) Destroy(_depthMat);
    }

    public void NotifyStep()
    {
        _stepCounter++;
        if (_cam != null)
            _cam.enabled = updateIntervalSteps <= 0 || _stepCounter % updateIntervalSteps == 0;
    }

    void OnRenderImage(RenderTexture src, RenderTexture dst)
    {
        if (_depthMat != null && DepthRT != null)
        {
            float sourceAspect = src.width / (float)src.height;
            float outputAspect = DepthRT.width / (float)DepthRT.height;
            Vector2 cropScale = Vector2.one;
            if (sourceAspect > outputAspect)
                cropScale.x = outputAspect / sourceAspect;
            else
                cropScale.y = sourceAspect / outputAspect;

            _depthMat.SetVector("_CropScale", cropScale);
            Graphics.Blit(src, DepthRT, _depthMat);
        }

        Graphics.Blit(src, dst);
    }
}
