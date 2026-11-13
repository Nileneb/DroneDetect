using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine.UI;

/// <summary>
/// One-click wiring for the existing Agent1 training scene. It keeps DroneCSI
/// on the component, disables it for this scene, and enables DroneFollow with
/// a projected animated PrefabBene target and no policy image sensors.
/// </summary>
public static class DroneFollowSetup
{
    const string TrainingScenePath = "Assets/Scenes/Agent1.unity";
    const string TargetPrefabPath = "Assets/Prefabs/PrefabBene.prefab";
    const string TargetInstanceName = "DroneFollowTarget";
    const string GroundPreviewName = "PiP_Ground";
    const string DepthPreviewName = "PiP_Depth";
    const string BoundingBoxOverlayName = "DroneFollowBoundingBoxOverlay";

    [MenuItem("DroneDetect/Configure Active Scene for DroneFollow")]
    public static void ConfigureActiveScene()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[DroneFollowSetup] Stop Play mode before configuring DroneFollow.");
            return;
        }

        var simulatedDrone = FindTrainingSceneDrone();
        if (simulatedDrone == null)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            EditorSceneManager.OpenScene(TrainingScenePath, OpenSceneMode.Single);
            simulatedDrone = FindTrainingSceneDrone();
        }

        if (simulatedDrone == null)
        {
            Debug.LogError($"[DroneFollowSetup] No active SimulatedDroneController was found in {TrainingScenePath}. Check the scene and drone prefab.");
            return;
        }

        GameObject droneObject = simulatedDrone.gameObject;
        var legacyAgent = droneObject.GetComponent<DroneAgent>();
        if (legacyAgent == null)
        {
            Debug.LogError("[DroneFollowSetup] DroneAgent with the existing target list was not found on the drone.");
            return;
        }

        // GroundCam looks down at the target area; prefer it over the legacy
        // forward/depth camera for projecting a person standing on the ground.
        Camera groundCamera = FindChildCamera(droneObject, "GroundCam");
        Camera camera = groundCamera != null
            ? groundCamera
            : legacyAgent.depthCamera != null
                ? legacyAgent.depthCamera.GetComponent<Camera>()
                : droneObject.GetComponentInChildren<Camera>(true);
        if (camera == null)
        {
            Debug.LogError("[DroneFollowSetup] No camera found on the drone or its children.");
            return;
        }

        Scene trainingScene = droneObject.scene;
        Undo.RecordObject(droneObject, "Configure DroneFollow");

        GameObject targetObject = FindNamedObject(trainingScene, TargetInstanceName);
        if (targetObject == null)
        {
            GameObject targetPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPrefabPath);
            if (targetPrefab == null)
            {
                Debug.LogError($"[DroneFollowSetup] Target prefab not found at {TargetPrefabPath}.");
                return;
            }

            targetObject = PrefabUtility.InstantiatePrefab(targetPrefab, trainingScene) as GameObject;
            if (targetObject == null)
            {
                Debug.LogError($"[DroneFollowSetup] Could not instantiate target prefab {TargetPrefabPath}.");
                return;
            }
            Undo.RegisterCreatedObjectUndo(targetObject, "Create DroneFollow target");
            targetObject.name = TargetInstanceName;

            Vector3 startPosition = droneObject.transform.position + droneObject.transform.forward * 4f;
            startPosition.y = 0f; // Agent1 ground plane is at world Y=0.
            targetObject.transform.SetPositionAndRotation(startPosition, Quaternion.identity);
        }

        Transform target = targetObject.transform;
        if (target.GetComponentInChildren<Renderer>(true) == null)
        {
            Debug.LogError($"[DroneFollowSetup] {TargetPrefabPath} has no Renderer in its hierarchy; detector projection cannot produce a box.");
            return;
        }

        if (legacyAgent.targets != null)
        {
            foreach (GameObject waypoint in legacyAgent.targets)
            {
                if (waypoint == null || waypoint == targetObject) continue;
                Undo.RecordObject(waypoint, "Hide DroneCSI waypoint");
                waypoint.SetActive(false);
            }
        }

        var behavior = droneObject.GetComponent<BehaviorParameters>();
        if (behavior == null)
        {
            Debug.LogError("[DroneFollowSetup] BehaviorParameters component is missing.");
            return;
        }

        // Drone.prefab and the inactive Shepherd prefab both contain camera
        // sensors. Remove them from this training scene so no Agent can update
        // a stale CameraSensor at episode end. Unity Camera components remain.
        int removedCameraSensors = RemoveCameraSensorComponents(trainingScene);

        if (groundCamera != null)
        {
            RawImage groundPreview = GetRawImage(trainingScene, GroundPreviewName);
            RenderTexture groundTexture = groundPreview != null ? groundPreview.texture as RenderTexture : null;
            if (groundTexture != null)
            {
                Undo.RecordObject(groundCamera, "Route GroundCam to PiP preview");
                groundCamera.targetTexture = groundTexture;
                groundCamera.targetDisplay = 0;
                EditorUtility.SetDirty(groundCamera);
            }
            else
            {
                Debug.LogWarning("[DroneFollowSetup] PiP_Ground does not reference a RenderTexture; GroundCam will still be used for projection, but its preview cannot be routed automatically.");
            }
        }

        var controllerAdapter = droneObject.GetComponent<SimulatedDroneFollowControllerAdapter>();
        if (controllerAdapter == null)
            controllerAdapter = Undo.AddComponent<SimulatedDroneFollowControllerAdapter>(droneObject);

        var detector = droneObject.GetComponent<SimulatedBoundingBoxDetector>();
        if (detector == null)
            detector = Undo.AddComponent<SimulatedBoundingBoxDetector>(droneObject);
        Undo.RecordObject(detector, "Configure simulated detector");
        detector.observationCamera = camera;
        detector.target = target;
        detector.sampleRateHz = DroneFollowContract.DecisionRateHz;
        detector.minLatencySeconds = 0.10f;
        detector.maxLatencySeconds = 0.20f;

        ConfigureBoundingBoxOverlay(trainingScene, camera, detector);

        var targetMotion = target.GetComponent<SimulatedMovingTarget>();
        if (targetMotion == null)
            targetMotion = Undo.AddComponent<SimulatedMovingTarget>(target.gameObject);
        Undo.RecordObject(targetMotion, "Configure moving target");

        var followAgent = droneObject.GetComponent<DroneFollowAgent>();
        if (followAgent == null)
            followAgent = Undo.AddComponent<DroneFollowAgent>(droneObject);
        Undo.RecordObject(followAgent, "Configure DroneFollow agent");
        followAgent.followTarget = target;
        followAgent.desiredAltitude = 1.5f;
        followAgent.desiredDistanceMin = 2f;
        followAgent.desiredDistanceMax = 3f;
        followAgent.enabled = true;

        legacyAgent.enabled = false;
        Undo.RecordObject(behavior, "Set DroneFollow behavior contract");
        behavior.BehaviorName = DroneFollowContract.BehaviorName;
        behavior.BehaviorType = BehaviorType.Default;
        behavior.UseChildSensors = false;

        var brainParameters = behavior.BrainParameters;
        brainParameters.VectorObservationSize = DroneFollowContract.ObservationSize;
        brainParameters.NumStackedVectorObservations = 1;
        brainParameters.ActionSpec = ActionSpec.MakeContinuous(DroneFollowContract.ActionSize);

        var requester = droneObject.GetComponent<DecisionRequester>();
        if (requester == null)
            requester = Undo.AddComponent<DecisionRequester>(droneObject);
        Undo.RecordObject(requester, "Set DroneFollow decision rate");
        float desiredPeriodSeconds = 1f / DroneFollowContract.DecisionRateHz;
        requester.DecisionPeriod = Mathf.Max(1, Mathf.RoundToInt(desiredPeriodSeconds / Time.fixedDeltaTime));
        requester.TakeActionsBetweenDecisions = true;
        float actualRateHz = 1f / (requester.DecisionPeriod * Time.fixedDeltaTime);
        if (Mathf.Abs(actualRateHz - DroneFollowContract.DecisionRateHz) > 0.1f)
            Debug.LogWarning($"[DroneFollowSetup] Fixed timestep gives {actualRateHz:F2} Hz, not the requested {DroneFollowContract.DecisionRateHz:F1} Hz.");

        EditorUtility.SetDirty(behavior);
        EditorUtility.SetDirty(detector);
        EditorUtility.SetDirty(targetMotion);
        EditorUtility.SetDirty(followAgent);
        EditorUtility.SetDirty(requester);
        EditorSceneManager.MarkSceneDirty(trainingScene);
        EditorSceneManager.SaveOpenScenes();
        Selection.activeGameObject = droneObject;

        Debug.Log($"[DroneFollowSetup] Agent1 configured: {DroneFollowContract.ObservationSize} observations, {DroneFollowContract.ActionSize} actions, {actualRateHz:F1} Hz; camera={camera.name}; removed {removedCameraSensors} CameraSensorComponent(s). The detector box is shown over the matching PiP preview.");
    }

    static Camera FindChildCamera(GameObject root, string cameraName)
    {
        foreach (Camera candidate in root.GetComponentsInChildren<Camera>(true))
        {
            if (candidate != null && candidate.name == cameraName)
                return candidate;
        }
        return null;
    }

    static RawImage GetRawImage(Scene scene, string objectName)
    {
        GameObject previewObject = FindNamedObject(scene, objectName);
        return previewObject != null ? previewObject.GetComponent<RawImage>() : null;
    }

    static void ConfigureBoundingBoxOverlay(Scene scene, Camera camera, SimulatedBoundingBoxDetector detector)
    {
        string previewName = camera.name == "GroundCam" ? GroundPreviewName : DepthPreviewName;
        RawImage preview = GetRawImage(scene, previewName);
        if (preview == null)
        {
            Debug.LogWarning($"[DroneFollowSetup] Could not find RawImage '{previewName}'; bounding boxes will still be computed but not drawn in the Game view.");
            return;
        }

        Transform existing = preview.transform.Find(BoundingBoxOverlayName);
        GameObject overlayObject;
        if (existing != null)
        {
            overlayObject = existing.gameObject;
        }
        else
        {
            overlayObject = new GameObject(
                BoundingBoxOverlayName,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(DroneFollowBoundingBoxOverlay));
            Undo.RegisterCreatedObjectUndo(overlayObject, "Create DroneFollow bounding box overlay");
            overlayObject.transform.SetParent(preview.transform, false);
        }

        RectTransform overlayRect = overlayObject.GetComponent<RectTransform>();
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;
        overlayRect.localScale = Vector3.one;
        overlayObject.transform.SetAsLastSibling();

        var overlay = overlayObject.GetComponent<DroneFollowBoundingBoxOverlay>();
        if (overlay == null)
            overlay = Undo.AddComponent<DroneFollowBoundingBoxOverlay>(overlayObject);
        Undo.RecordObject(overlay, "Configure DroneFollow bounding box overlay");
        overlay.detector = detector;
        overlay.lineThickness = 2.5f;
        overlay.color = new Color(0.18f, 1f, 0.28f, 1f);
        overlay.raycastTarget = false;
        EditorUtility.SetDirty(overlay);
        EditorUtility.SetDirty(overlayRect);
    }

    static int RemoveCameraSensorComponents(Scene scene)
    {
        int removed = 0;
        if (!scene.IsValid() || !scene.isLoaded)
            return removed;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            var sensorComponents = root.GetComponentsInChildren<CameraSensorComponent>(true);
            foreach (CameraSensorComponent sensor in sensorComponents)
            {
                if (sensor == null) continue;
                Undo.DestroyObjectImmediate(sensor);
                removed++;
            }
        }

        return removed;
    }

    static GameObject FindNamedObject(Scene scene, string objectName)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return null;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == objectName)
                return root;

            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == objectName)
                    return child.gameObject;
            }
        }

        return null;
    }

    static SimulatedDroneController FindTrainingSceneDrone()
    {
        Scene scene = SceneManager.GetSceneByPath(TrainingScenePath);
        if (!scene.IsValid() || !scene.isLoaded)
            return null;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (SimulatedDroneController controller in root.GetComponentsInChildren<SimulatedDroneController>(true))
            {
                if (controller != null && controller.gameObject.activeInHierarchy)
                    return controller;
            }
        }

        return null;
    }
}
