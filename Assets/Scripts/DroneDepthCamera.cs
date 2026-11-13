using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Generates a grayscale depth view from the same camera render used for the
/// main RGB image. The depth preview is visualization only, not a policy sensor.
/// </summary>
[RequireComponent(typeof(Camera))]
public class DroneDepthCamera : MonoBehaviour
{
    [Header("Depth Preview")]
    public RawImage depthPreview;
    [Min(32)] public int resolution = 256;
    [Min(0.1f)] public float maxVisualizationDistance = 20f;
    [Min(0)] public int updateIntervalSteps = 5;

    public RenderTexture DepthRT { get; private set; }

    Camera _cam;
    Material _depthMat;
    int _stepCounter;

    void Awake()
    {
        _cam = GetComponent<Camera>();
        _cam.depthTextureMode = DepthTextureMode.Depth;

        float aspect = _cam.aspect > 0f ? _cam.aspect : 1f;
        int width = Mathf.Max(32, resolution);
        int height = Mathf.Max(32, Mathf.RoundToInt(width / aspect));
        DepthRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
        DepthRT.name = "DroneDepthRT";
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
        if (DepthRT != null) DepthRT.Release();
        if (_depthMat != null) Destroy(_depthMat);
    }

    public void NotifyStep()
    {
        if (updateIntervalSteps <= 0) return;
        _stepCounter++;
        _cam.enabled = _stepCounter % updateIntervalSteps == 0;
    }

    void OnRenderImage(RenderTexture src, RenderTexture dst)
    {
        if (_depthMat != null)
            Graphics.Blit(src, DepthRT, _depthMat);

        Graphics.Blit(src, dst);
    }
}
