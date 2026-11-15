using Unity.InferenceEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Generates the phone ground-station scene from scratch: AR.Drone controller,
/// external-frame perception, the trained policy in inference-only deployment
/// mode, and the operator UI. Regenerate after changing any of these parts.
/// </summary>
public static class DroneFollowGroundStationSetup
{
    public const string ScenePath = "Assets/Scenes/DroneFollowAndroid.unity";
    public const string PolicyModelPath = "Assets/Models/DroneFollow.onnx";
    const string MidasSmallModelPath = "Assets/Models/model-small.onnx";
    const string MidasLargeModelPath = "Assets/Models/model-f6b98070.onnx";
    const string YoloModelPath = "Assets/Models/yolov8n.onnx";
    const string ReidModelPath = "Assets/Models/osnet_reid.onnx";
    const string RgbTexturePath = "Assets/RenderTextures/MainCameraRGB.renderTexture";
    const string DepthTexturePath = "Assets/RenderTextures/PiP_DepthCam.renderTexture";

    [MenuItem("DroneDetect/Create Android Ground Station Scene")]
    static void CreateSceneMenu() => CreateScene();

    /// <summary>Builds and saves the scene; returns false (after logging why) if an asset is missing.</summary>
    public static bool CreateScene()
    {
        var policy = AssetDatabase.LoadAssetAtPath<ModelAsset>(PolicyModelPath);
        var midasSmall = AssetDatabase.LoadAssetAtPath<ModelAsset>(MidasSmallModelPath);
        var midasLarge = AssetDatabase.LoadAssetAtPath<ModelAsset>(MidasLargeModelPath);
        var yolo = AssetDatabase.LoadAssetAtPath<ModelAsset>(YoloModelPath);
        var reid = AssetDatabase.LoadAssetAtPath<ModelAsset>(ReidModelPath);
        var rgb = AssetDatabase.LoadAssetAtPath<RenderTexture>(RgbTexturePath);
        var depth = AssetDatabase.LoadAssetAtPath<RenderTexture>(DepthTexturePath);
        if (policy == null)
        {
            Debug.LogError($"[GroundStationSetup] Copy the trained policy to {PolicyModelPath} first (results/<run-id>/DroneFollow.onnx).");
            return false;
        }
        if (midasSmall == null || yolo == null || reid == null || rgb == null || depth == null)
        {
            Debug.LogError("[GroundStationSetup] MiDaS/YOLO/OSNet models or the RGB/depth RenderTextures are missing; see README 'Models'.");
            return false;
        }

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var cameraObject = new GameObject("UI Camera");
        var camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.cullingMask = 0;

        var station = new GameObject("GroundStation");
        var drone = station.AddComponent<ArDroneFollowController>();

        var perception = station.AddComponent<DroneFollowSentisPerception>();
        perception.frameSource = DroneFollowSentisPerception.FrameSource.External;
        perception.midasSmallModel = midasSmall;
        perception.midasLargeModel = midasLarge;
        perception.yoloModel = yolo;
        perception.depthModelChoice = DroneFollowSentisPerception.DepthModelChoice.Small;
        perception.rgbInput = rgb;
        perception.depthOutput = depth;
        perception.detectionInferenceRateHz = DroneFollowContract.DecisionRateHz;
        perception.randomizeDetections = false;
        perception.autoLockTarget = false;
        perception.reidModel = reid;
        drone.perception = perception;

        var behavior = station.AddComponent<BehaviorParameters>();
        behavior.BehaviorName = DroneFollowContract.BehaviorName;
        behavior.BehaviorType = BehaviorType.InferenceOnly;
        behavior.Model = policy;
        behavior.UseChildSensors = false;
        BrainParameters brain = behavior.BrainParameters;
        brain.VectorObservationSize = DroneFollowContract.ObservationSize;
        brain.NumStackedVectorObservations = 1;
        brain.ActionSpec = ActionSpec.MakeContinuous(DroneFollowContract.ActionSize);

        // The sensor name and settings must match the training scene, or the policy rejects the observation.
        var depthSensor = station.AddComponent<RenderTextureSensorComponent>();
        depthSensor.RenderTexture = depth;
        depthSensor.SensorName = "DroneFollowDepth";
        depthSensor.Grayscale = true;
        depthSensor.ObservationStacks = 1;
        depthSensor.CompressionType = SensorCompressionType.None;

        var agent = station.AddComponent<DroneFollowAgent>();
        agent.deploymentMode = true;
        agent.perception = perception;

        var requester = station.AddComponent<DecisionRequester>();
        requester.DecisionPeriod = Mathf.Max(1, Mathf.RoundToInt(1f / (DroneFollowContract.DecisionRateHz * Time.fixedDeltaTime)));
        requester.TakeActionsBetweenDecisions = true;

        var ui = station.AddComponent<GroundStationUI>();
        ui.drone = drone;
        ui.perception = perception;
        ui.agent = agent;

        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"[GroundStationSetup] Saved {ScenePath} with policy '{policy.name}'.");
        return true;
    }
}
