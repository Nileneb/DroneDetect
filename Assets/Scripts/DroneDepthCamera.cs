using UnityEngine;
using Unity.MLAgents.Sensors;

/// <summary>
/// Renders a linear-depth view every N agent steps into a RenderTexture.
/// Attach to a child GameObject on the drone with a Camera component.
/// Automatically wires its RenderTexture into a RenderTextureSensorComponent
/// on the same GameObject (adds the component if missing).
/// </summary>
[RequireComponent(typeof(Camera))]
public class DroneDepthCamera : MonoBehaviour
{
    [Header("Depth Capture")]
    [Tooltip("Update depth texture every N agent steps (0 = every frame).")]
    public int depthUpdateInterval = 5;

    [Tooltip("Resolution of the depth render texture.")]
    public int resolution = 32;

    public RenderTexture DepthRT { get; private set; }

    Camera _cam;
    Material _depthMat;
    int _stepCounter;

    void Awake()
    {
        _cam = GetComponent<Camera>();
        _cam.depthTextureMode = DepthTextureMode.Depth;

        DepthRT = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32);
        DepthRT.name = "DroneDepthRT";
        DepthRT.Create();

        Shader shader = Shader.Find("DroneDetect/DepthVisualize");
        if (shader == null)
            Debug.LogError("[DroneDepthCamera] DepthVisualize shader not found. Check Assets/Shaders/.");
        else
            _depthMat = new Material(shader);

        WireUpSensor();
    }

    void WireUpSensor()
    {
        var sensor = GetComponent<RenderTextureSensorComponent>();
        if (sensor == null)
            sensor = gameObject.AddComponent<RenderTextureSensorComponent>();

        sensor.RenderTexture = DepthRT;
        sensor.SensorName = "DepthSensor";
        sensor.Grayscale = true;
    }

    void OnDestroy()
    {
        if (DepthRT != null) DepthRT.Release();
        if (_depthMat != null) Destroy(_depthMat);
    }

    /// <summary>Called by DroneAgent.OnActionReceived to throttle captures.</summary>
    public void NotifyStep()
    {
        if (depthUpdateInterval <= 0) return;
        _stepCounter++;
        _cam.enabled = (_stepCounter % depthUpdateInterval == 0);
    }

    void OnRenderImage(RenderTexture src, RenderTexture dst)
    {
        if (_depthMat != null)
            Graphics.Blit(src, DepthRT, _depthMat);

        Graphics.Blit(src, dst);
    }
}
