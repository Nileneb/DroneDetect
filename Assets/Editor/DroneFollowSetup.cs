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
/// One-click wiring for the existing Agent1 training scene. It configures
/// DroneFollow with a projected animated PrefabBene target and no policy image
/// sensors.
/// </summary>
public static class DroneFollowSetup
{
    const string TrainingScenePath = "Assets/Scenes/Agent1.unity";
    const string TargetPrefabPath = "Assets/Prefabs/PrefabBene.prefab";
    const string TargetInstanceName = "DroneFollowTarget";
    const string GroundPreviewName = "PiP_Ground";
    const string DepthPreviewName = "PiP_Depth";
    const string BoundingBoxOverlayName = "DroneFollowBoundingBoxOverlay";
    const string BoundingBoxOverlayCanvasName = "DroneFollowBoundingBoxOverlayCanvas";

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

        // Main Camera supplies RGB, detection, the box overlay, and the depth
        // preview. GroundCam remains a separate downward-looking preview.
        Camera mainCamera = FindChildCamera(droneObject, "Main Camera");
        Camera groundCamera = FindChildCamera(droneObject, "GroundCam");
        Camera camera = mainCamera != null
            ? mainCamera
            : legacyAgent != null && legacyAgent.depthCamera != null
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

        if (legacyAgent != null && legacyAgent.targets != null)
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
            Undo.RecordObject(groundCamera, "Set GroundCam background to white");
            groundCamera.clearFlags = CameraClearFlags.SolidColor;
            groundCamera.backgroundColor = Color.white;

            RawImage groundPreview = GetRawImage(trainingScene, GroundPreviewName);
            RenderTexture groundTexture = groundPreview != null ? groundPreview.texture as RenderTexture : null;
            if (groundTexture != null)
            {
                groundCamera.targetTexture = groundTexture;
                groundCamera.targetDisplay = 0;
            }
            else
            {
                Debug.LogWarning("[DroneFollowSetup] PiP_Ground does not reference a RenderTexture; GroundCam will still be used for projection, but its preview cannot be routed automatically.");
            }
            EditorUtility.SetDirty(groundCamera);
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

        RawImage depthPreview = GetRawImage(trainingScene, DepthPreviewName);
        var depthProcessor = camera.GetComponent<DroneDepthCamera>();
        if (depthProcessor == null)
            depthProcessor = Undo.AddComponent<DroneDepthCamera>(camera.gameObject);
        Undo.RecordObject(depthProcessor, "Generate depth preview from Main Camera");
        depthProcessor.depthPreview = depthPreview;
        depthProcessor.resolution = 256;
        depthProcessor.maxVisualizationDistance = 20f;
        depthProcessor.enabled = true;

        Camera separateDepthCamera = FindChildCamera(droneObject, "DepthCamera");
        if (separateDepthCamera != null && separateDepthCamera != camera)
        {
            Undo.RecordObject(separateDepthCamera, "Disable separate DroneFollow depth camera");
            separateDepthCamera.enabled = false;
            DroneDepthCamera separateDepthProcessor = separateDepthCamera.GetComponent<DroneDepthCamera>();
            if (separateDepthProcessor != null)
            {
                Undo.RecordObject(separateDepthProcessor, "Disable separate DroneFollow depth processor");
                separateDepthProcessor.enabled = false;
                EditorUtility.SetDirty(separateDepthProcessor);
            }
            EditorUtility.SetDirty(separateDepthCamera);
        }

        ConfigureBoundingBoxOverlay(trainingScene, detector);

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

        // DecisionRequester binds to the first Agent returned by GetComponent.
        // Keeping multiple Agent components on this GameObject routes requests
        // to the legacy component instead of DroneFollowAgent.
        if (legacyAgent != null)
            Undo.DestroyObjectImmediate(legacyAgent);
        var shepherdAgent = droneObject.GetComponent<ShepherdDroneAgent>();
        if (shepherdAgent != null)
            Undo.DestroyObjectImmediate(shepherdAgent);

        if (droneObject.GetComponent<Agent>() != followAgent)
        {
            Debug.LogError("[DroneFollowSetup] DroneFollowAgent is not the only Agent on the drone; DecisionRequester would target a different Agent.");
            return;
        }

        Undo.RecordObject(behavior, "Set DroneFollow behavior contract");
        behavior.BehaviorName = DroneFollowContract.BehaviorName;
        behavior.BehaviorType = BehaviorType.Default;
        behavior.UseChildSensors = false;

        var brainParameters = behavior.BrainParameters;
        brainParameters.VectorObservationSize = DroneFollowContract.ObservationSize;
        brainParameters.NumStackedVectorObservations = 1;
        brainParameters.ActionSpec = new ActionSpec(
            DroneFollowContract.ActionSize,
            new[] { DroneFollowContract.DiscreteActionCount });

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
        EditorUtility.SetDirty(depthProcessor);
        EditorUtility.SetDirty(targetMotion);
        EditorUtility.SetDirty(followAgent);
        EditorUtility.SetDirty(requester);
        EditorSceneManager.MarkSceneDirty(trainingScene);
        EditorSceneManager.SaveOpenScenes();
        Selection.activeGameObject = droneObject;

        Debug.Log($"[DroneFollowSetup] Agent1 configured: {DroneFollowContract.ObservationSize} observations, {DroneFollowContract.ActionSize} continuous actions + {DroneFollowContract.DiscreteBranchSize} discrete branch, {actualRateHz:F1} Hz; Main Camera drives detection, RGB display and depth preview; GroundCam remains downward-facing; separate depth camera disabled; removed {removedCameraSensors} CameraSensorComponent(s).");
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

    static void ConfigureBoundingBoxOverlay(Scene scene, SimulatedBoundingBoxDetector detector)
    {
        GameObject canvasObject = FindNamedObject(scene, BoundingBoxOverlayCanvasName);
        if (canvasObject == null)
        {
            canvasObject = new GameObject(
                BoundingBoxOverlayCanvasName,
                typeof(RectTransform),
                typeof(Canvas));
            Undo.RegisterCreatedObjectUndo(canvasObject, "Create DroneFollow screen overlay");
        }

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 98;

        GameObject overlayObject = FindNamedObject(scene, BoundingBoxOverlayName);
        if (overlayObject == null)
        {
            overlayObject = new GameObject(
                BoundingBoxOverlayName,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(DroneFollowBoundingBoxOverlay));
            Undo.RegisterCreatedObjectUndo(overlayObject, "Create DroneFollow bounding box overlay");
        }
        Undo.RecordObject(overlayObject.transform, "Show bounding box on Main Camera");
        overlayObject.transform.SetParent(canvasObject.transform, false);

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
        EditorUtility.SetDirty(canvas);
        EditorUtility.SetDirty(canvasObject.transform);
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
