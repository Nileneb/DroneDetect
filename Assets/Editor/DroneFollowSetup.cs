using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine.UI;
using Unity.InferenceEngine;

/// <summary>
/// One-click wiring for the existing Agent1 training scene. It configures
/// DroneFollow with Main Camera Sentis perception and a centered grayscale
/// depth observation.
/// </summary>
public static class DroneFollowSetup
{
    const string TrainingScenePath = "Assets/Scenes/Agent1.unity";
    const string TargetPrefabPath = "Assets/Prefabs/PrefabBene.prefab";
    const string MidasLargeModelAssetPath = "Assets/Models/model-f6b98070.onnx";
    const string MidasSmallModelAssetPath = "Assets/Models/model-small.onnx";
    const string YoloModelAssetPath = "Assets/Models/yolov8n.onnx";
    const string DepthTextureAssetPath = "Assets/RenderTextures/PiP_DepthCam.renderTexture";
    const string RgbTextureAssetPath = "Assets/RenderTextures/MainCameraRGB.renderTexture";
    const string TargetInstanceName = "DroneFollowTarget";
    const string MainCameraCanvasName = "DroneFollowMainCameraCanvas";
    const string LegacyControlPanelName = "DroneControlPanel";
    const string MainCameraPreviewName = "DroneFollowMainCameraPreview";
    const string MainCameraScreenName = "Screen1_MainCamera";
    const string DepthScreenName = "Screen2_Depth";
    const string GroundScreenName = "Screen3_Ground";
    const string MainCameraContentName = "MainCameraContent";
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
        if (mainCamera == null)
        {
            Debug.LogError("[DroneFollowSetup] A child camera named 'Main Camera' was not found. Perception was not configured; no fallback camera will be used.");
            return;
        }
        Camera camera = mainCamera;

        Scene trainingScene = droneObject.scene;
        int removedMissingScripts = RemoveMissingScriptsInScene(trainingScene);
        int remainingMissingScripts = CountMissingScriptsInScene(trainingScene);
        if (remainingMissingScripts > 0)
        {
            Debug.LogError(
                $"[DroneFollowSetup] Removed {removedMissingScripts} missing script component(s), but {remainingMissingScripts} remain in the training scene. Repair the source prefab(s) before configuring the scene.");
            return;
        }

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
            RecordPrefabInstanceOverride(targetObject.transform);
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
        RawImage groundPreview = GetRawImage(trainingScene, GroundPreviewName);
        RenderTexture groundTexture = groundPreview != null
            ? groundPreview.texture as RenderTexture
            : AssetDatabase.LoadAssetAtPath<RenderTexture>("Assets/RenderTextures/PiP_GroundCam.renderTexture");

        if (groundCamera != null)
        {
            Undo.RecordObject(groundCamera, "Set GroundCam background to white");
            groundCamera.clearFlags = CameraClearFlags.SolidColor;
            groundCamera.backgroundColor = Color.white;

            if (groundTexture != null)
            {
                groundCamera.targetTexture = groundTexture;
                groundCamera.targetDisplay = 0;
            }
            else
            {
                Debug.LogWarning("[DroneFollowSetup] PiP_Ground does not reference a RenderTexture; GroundCam will still be used for projection, but its preview cannot be routed automatically.");
            }
            RecordPrefabInstanceOverride(groundCamera);
            EditorUtility.SetDirty(groundCamera);
        }

        Camera displayCamera = droneObject.GetComponent<Camera>();
        if (displayCamera != null)
        {
            Undo.RecordObject(displayCamera, "Turn Drone root camera into an empty display camera");
            // WHY(training-speed, camera): Screen 1 shows Main Camera through MainCameraRGB on an overlay canvas, so this
            // camera rendered the whole scene a second time underneath it. It only stays to keep Display 1 "rendering"
            // (disabling it shows Unity's "No cameras rendering" banner), so it must draw nothing.
            displayCamera.cullingMask = 0;
            displayCamera.clearFlags = CameraClearFlags.SolidColor;
            displayCamera.backgroundColor = Color.black;
            displayCamera.depth = -100f;
            displayCamera.targetTexture = null;
            displayCamera.targetDisplay = 0;
            displayCamera.useOcclusionCulling = false;
            displayCamera.allowHDR = false;
            displayCamera.allowMSAA = false;
            RecordPrefabInstanceOverride(displayCamera);
            EditorUtility.SetDirty(displayCamera);
        }

        var controllerAdapter = droneObject.GetComponent<SimulatedDroneFollowControllerAdapter>();
        if (controllerAdapter == null)
            controllerAdapter = Undo.AddComponent<SimulatedDroneFollowControllerAdapter>(droneObject);

        ModelAsset midasLargeModel = AssetDatabase.LoadAssetAtPath<ModelAsset>(MidasLargeModelAssetPath);
        ModelAsset midasSmallModel = AssetDatabase.LoadAssetAtPath<ModelAsset>(MidasSmallModelAssetPath);
        ModelAsset yoloModel = AssetDatabase.LoadAssetAtPath<ModelAsset>(YoloModelAssetPath);
        if (midasLargeModel == null || midasSmallModel == null || yoloModel == null)
        {
            Debug.LogError(
                $"[DroneFollowSetup] Required models are missing. Expected {MidasLargeModelAssetPath}, {MidasSmallModelAssetPath}, and {YoloModelAssetPath}.");
            return;
        }

        RenderTexture depthTexture = AssetDatabase.LoadAssetAtPath<RenderTexture>(DepthTextureAssetPath);
        if (depthTexture == null)
        {
            Debug.LogError($"[DroneFollowSetup] The depth observation RenderTexture is missing. Expected a RenderTexture at {DepthTextureAssetPath}.");
            return;
        }

        if (depthTexture.width != DroneFollowContract.DepthObservationWidth ||
            depthTexture.height != DroneFollowContract.DepthObservationHeight)
        {
            Debug.LogError(
                $"[DroneFollowSetup] Depth RenderTexture must be {DroneFollowContract.DepthObservationWidth}x{DroneFollowContract.DepthObservationHeight}, but is {depthTexture.width}x{depthTexture.height}. Resize the asset before configuring the scene.");
            return;
        }

        RenderTexture rgbTexture = AssetDatabase.LoadAssetAtPath<RenderTexture>(RgbTextureAssetPath);
        if (rgbTexture == null || rgbTexture.width != 640 || rgbTexture.height != 360)
        {
            Debug.LogError($"[DroneFollowSetup] Expected a 640x360 RGB RenderTexture at {RgbTextureAssetPath}.");
            return;
        }

        RawImage mainCameraPreview = ConfigureMonitorLayout(
            trainingScene,
            rgbTexture,
            depthTexture,
            groundTexture,
            out RectTransform mainCameraContent);
        RawImage depthPreview = GetRawImage(trainingScene, DepthPreviewName);
        if (depthPreview == null)
        {
            Debug.LogError($"[DroneFollowSetup] Depth preview RawImage '{DepthPreviewName}' was not created.");
            return;
        }

        DisableLegacyControlPanel(trainingScene);
        Undo.RecordObject(camera, "Route Main Camera through RGB RenderTexture");
        camera.targetTexture = rgbTexture;
        camera.targetDisplay = 0;
        EditorUtility.SetDirty(camera);
        if (PrefabUtility.IsPartOfPrefabInstance(camera))
            PrefabUtility.RecordPrefabInstancePropertyModifications(camera);

        foreach (DroneFollowSentisPerception stalePerception in
                 droneObject.GetComponentsInChildren<DroneFollowSentisPerception>(true))
        {
            if (stalePerception != null && stalePerception.gameObject != camera.gameObject)
                Undo.DestroyObjectImmediate(stalePerception);
        }

        DroneFollowSentisPerception sentisPerception = camera.GetComponent<DroneFollowSentisPerception>();
        if (sentisPerception == null)
            sentisPerception = Undo.AddComponent<DroneFollowSentisPerception>(camera.gameObject);
        Undo.RecordObject(sentisPerception, "Configure Main Camera Sentis perception");
        sentisPerception.midasLargeModel = midasLargeModel;
        sentisPerception.midasSmallModel = midasSmallModel;
        sentisPerception.yoloModel = yoloModel;
        sentisPerception.depthModelChoice = DroneFollowSentisPerception.DepthModelChoice.Small;
        sentisPerception.observationCamera = camera;
        sentisPerception.rgbInput = rgbTexture;
        sentisPerception.depthOutput = depthTexture;
        sentisPerception.depthPreview = depthPreview;
        sentisPerception.depthInferenceRateHz = 15f;
        sentisPerception.detectionInferenceRateHz = DroneFollowContract.DecisionRateHz;
        sentisPerception.personScoreThreshold = 0.2f;
        sentisPerception.enabled = true;
        RecordPrefabInstanceOverride(sentisPerception);
        EditorUtility.SetDirty(sentisPerception);

        Camera separateDepthCamera = FindChildCamera(droneObject, "DepthCamera");
        if (separateDepthCamera != null && separateDepthCamera != camera)
        {
            Undo.RecordObject(separateDepthCamera, "Disable obsolete separate depth camera");
            separateDepthCamera.enabled = false;
            EditorUtility.SetDirty(separateDepthCamera);
        }

        var depthSensor = droneObject.GetComponent<RenderTextureSensorComponent>();
        if (depthSensor == null)
            depthSensor = Undo.AddComponent<RenderTextureSensorComponent>(droneObject);
        Undo.RecordObject(depthSensor, "Add centered grayscale depth observation");
        depthSensor.RenderTexture = depthTexture;
        depthSensor.SensorName = "DroneFollowDepth";
        depthSensor.Grayscale = true;
        depthSensor.ObservationStacks = 1;
        depthSensor.CompressionType = SensorCompressionType.None;
        RecordPrefabInstanceOverride(depthSensor);

        ConfigureBoundingBoxOverlay(trainingScene, sentisPerception, mainCameraContent);

        var targetMotion = target.GetComponent<SimulatedMovingTarget>();
        if (targetMotion == null)
            targetMotion = Undo.AddComponent<SimulatedMovingTarget>(target.gameObject);
        Undo.RecordObject(targetMotion, "Configure moving target");
        targetMotion.roamRadius = 12f;
        targetMotion.minSpeed = 0.75f;
        targetMotion.maxSpeed = 2f;
        targetMotion.stopProbability = 0.05f;
        targetMotion.minStopSeconds = 0.2f;
        targetMotion.maxStopSeconds = 0.8f;
        RecordPrefabInstanceOverride(targetMotion);

        var followAgent = droneObject.GetComponent<DroneFollowAgent>();
        if (followAgent == null)
            followAgent = Undo.AddComponent<DroneFollowAgent>(droneObject);
        Undo.RecordObject(followAgent, "Configure DroneFollow agent");
        followAgent.followTarget = target;
        followAgent.perception = sentisPerception;
        followAgent.desiredAltitude = 1.5f;
        followAgent.desiredDistanceMin = 2f;
        followAgent.desiredDistanceMax = 3f;
        followAgent.safeLandingMinimumFlightFraction = 1f;
        followAgent.enabled = true;
        RecordPrefabInstanceOverride(followAgent);

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
        if (followAgent.perception != sentisPerception ||
            sentisPerception.gameObject != mainCamera.gameObject ||
            mainCamera.targetTexture != rgbTexture ||
            sentisPerception.rgbInput != rgbTexture ||
            mainCameraPreview.texture != rgbTexture ||
            sentisPerception.depthOutput != depthSensor.RenderTexture)
        {
            Debug.LogError("[DroneFollowSetup] RGB/depth perception wiring did not persist on Main Camera, its preview, and the Agent depth sensor.");
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
        RecordPrefabInstanceOverride(behavior);

        var requester = droneObject.GetComponent<DecisionRequester>();
        if (requester == null)
            requester = Undo.AddComponent<DecisionRequester>(droneObject);
        Undo.RecordObject(requester, "Set DroneFollow decision rate");
        float desiredPeriodSeconds = 1f / DroneFollowContract.DecisionRateHz;
        requester.DecisionPeriod = Mathf.Max(1, Mathf.RoundToInt(desiredPeriodSeconds / Time.fixedDeltaTime));
        requester.TakeActionsBetweenDecisions = true;
        RecordPrefabInstanceOverride(requester);
        float actualRateHz = 1f / (requester.DecisionPeriod * Time.fixedDeltaTime);
        if (Mathf.Abs(actualRateHz - DroneFollowContract.DecisionRateHz) > 0.1f)
            Debug.LogWarning($"[DroneFollowSetup] Fixed timestep gives {actualRateHz:F2} Hz, not the requested {DroneFollowContract.DecisionRateHz:F1} Hz.");

        EditorUtility.SetDirty(behavior);
        EditorUtility.SetDirty(sentisPerception);
        EditorUtility.SetDirty(depthSensor);
        EditorUtility.SetDirty(targetMotion);
        EditorUtility.SetDirty(followAgent);
        EditorUtility.SetDirty(requester);
        EditorSceneManager.MarkSceneDirty(trainingScene);
        EditorSceneManager.SaveOpenScenes();
        Selection.activeGameObject = droneObject;

        Debug.Log($"[DroneFollowSetup] Agent1 configured: perception on '{mainCamera.name}' is assigned to DroneFollowAgent and writes to its grayscale depth sensor; {DroneFollowContract.ObservationSize} vector observations + {DroneFollowContract.DepthObservationWidth}x{DroneFollowContract.DepthObservationHeight} image, {DroneFollowContract.ActionSize} continuous actions + {DroneFollowContract.DiscreteBranchSize} discrete branch, {actualRateHz:F1} Hz; removed {removedCameraSensors} CameraSensorComponent(s) and {removedMissingScripts} missing script(s).");
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

    static RawImage ConfigureMonitorLayout(
        Scene scene,
        RenderTexture rgbTexture,
        RenderTexture depthTexture,
        RenderTexture groundTexture,
        out RectTransform mainCameraContent)
    {
        GameObject canvasObject = FindNamedObject(scene, MainCameraCanvasName);
        if (canvasObject == null)
        {
            canvasObject = new GameObject(
                MainCameraCanvasName,
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            Undo.RegisterCreatedObjectUndo(canvasObject, "Create DroneFollow monitor canvas");
        }

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 50;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        if (scaler == null)
            scaler = Undo.AddComponent<CanvasScaler>(canvasObject);
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        Transform mainScreen = ConfigureMonitorCard(
            scene,
            canvasObject.transform,
            MainCameraScreenName,
            "SCREEN 1  |  MAIN CAMERA",
            Vector2.zero,
            Vector2.one,
            new Vector2(0f, 0f),
            Vector2.one);
        mainCameraContent = EnsureRectTransform(mainScreen, MainCameraContentName);
        SetStretch(mainCameraContent, Vector2.zero, Vector2.one);

        RawImage mainPreview = GetOrCreateRawImage(scene, mainCameraContent, MainCameraPreviewName);
        ConfigureScreenImage(mainPreview, rgbTexture);

        Transform depthScreen = ConfigureMonitorCard(
            scene,
            mainCameraContent,
            DepthScreenName,
            "SCREEN 2  |  DEPTH FROM MAIN CAM",
            new Vector2(0.78f, 0.62f),
            new Vector2(0.98f, 0.96f),
            Vector2.zero,
            Vector2.one);
        RectTransform depthContent = EnsureRectTransform(depthScreen, "DepthContent");
        SetStretch(depthContent, Vector2.zero, Vector2.one);
        RawImage depthPreview = GetOrCreateRawImage(scene, depthContent, DepthPreviewName);
        ConfigureScreenImage(depthPreview, depthTexture);

        Transform groundScreen = ConfigureMonitorCard(
            scene,
            mainCameraContent,
            GroundScreenName,
            "SCREEN 3  |  GROUND CAM",
            new Vector2(0.78f, 0.26f),
            new Vector2(0.98f, 0.60f),
            Vector2.zero,
            Vector2.one);
        RectTransform groundContent = EnsureRectTransform(groundScreen, "GroundContent");
        SetStretch(groundContent, Vector2.zero, Vector2.one);
        RawImage groundPreview = GetOrCreateRawImage(scene, groundContent, GroundPreviewName);
        ConfigureScreenImage(groundPreview, groundTexture);

        SetPreviewDecorationActive(scene, "PiP_Ground_border", false);
        SetPreviewDecorationActive(scene, "PiP_Ground_label", false);
        SetPreviewDecorationActive(scene, "PiP_Depth_border", false);
        SetPreviewDecorationActive(scene, "PiP_Depth_label", false);

        EditorUtility.SetDirty(canvas);
        EditorUtility.SetDirty(scaler);
        EditorUtility.SetDirty(canvasObject.transform);
        return mainPreview;
    }

    static Transform ConfigureMonitorCard(
        Scene scene,
        Transform canvas,
        string objectName,
        string title,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 imageAnchorMin,
        Vector2 imageAnchorMax)
    {
        GameObject screen = FindNamedObject(scene, objectName);
        if (screen == null)
            screen = EnsureChild(canvas, objectName, typeof(RectTransform), typeof(Image));
        else
        {
            if (screen.transform.parent != canvas)
            {
                Undo.RecordObject(screen.transform, "Arrange DroneFollow picture-in-picture screens");
                screen.transform.SetParent(canvas, false);
            }
            if (screen.GetComponent<Image>() == null)
                Undo.AddComponent<Image>(screen);
        }

        RectTransform screenRect = screen.GetComponent<RectTransform>();
        screenRect.anchorMin = anchorMin;
        screenRect.anchorMax = anchorMax;
        screenRect.offsetMin = Vector2.zero;
        screenRect.offsetMax = Vector2.zero;
        screenRect.localScale = Vector3.one;

        Image background = screen.GetComponent<Image>();
        background.color = new Color(0.025f, 0.035f, 0.05f, 0.96f);
        background.raycastTarget = false;

        GameObject header = EnsureChild(screen.transform, "Header", typeof(RectTransform), typeof(Image));
        RectTransform headerRect = header.GetComponent<RectTransform>();
        headerRect.anchorMin = new Vector2(0f, objectName == MainCameraScreenName ? 0.965f : 0.86f);
        headerRect.anchorMax = Vector2.one;
        headerRect.offsetMin = Vector2.zero;
        headerRect.offsetMax = Vector2.zero;
        Image headerImage = header.GetComponent<Image>();
        headerImage.color = new Color(0.07f, 0.12f, 0.17f, 1f);
        headerImage.raycastTarget = false;

        GameObject label = EnsureChild(header.transform, "Title", typeof(RectTransform), typeof(Text));
        RectTransform labelRect = label.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(16f, 0f);
        labelRect.offsetMax = new Vector2(-8f, 0f);
        Text text = label.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = objectName == MainCameraScreenName ? 20 : 12;
        text.fontStyle = FontStyle.Bold;
        text.alignment = TextAnchor.MiddleLeft;
        text.color = new Color(0.72f, 0.91f, 1f);
        text.text = title;
        text.raycastTarget = false;

        GameObject imageArea = EnsureChild(screen.transform, "ImageArea", typeof(RectTransform));
        RectTransform imageAreaRect = imageArea.GetComponent<RectTransform>();
        SetStretch(imageAreaRect, imageAnchorMin, imageAnchorMax);
        imageAreaRect.localScale = Vector3.one;
        header.transform.SetAsLastSibling();
        return imageArea.transform;
    }

    static GameObject EnsureChild(Transform parent, string objectName, params System.Type[] components)
    {
        Transform child = parent.Find(objectName);
        GameObject childObject;
        if (child != null)
            childObject = child.gameObject;
        else
        {
            childObject = new GameObject(objectName, components);
            Undo.RegisterCreatedObjectUndo(childObject, "Create DroneFollow monitor element");
        }

        if (childObject.transform.parent != parent)
        {
            Undo.RecordObject(childObject.transform, "Arrange DroneFollow monitors");
            childObject.transform.SetParent(parent, false);
        }

        foreach (System.Type componentType in components)
        {
            if (childObject.GetComponent(componentType) == null)
                Undo.AddComponent(childObject, componentType);
        }

        return childObject;
    }

    static RectTransform EnsureRectTransform(Transform parent, string objectName)
    {
        GameObject child = EnsureChild(parent, objectName, typeof(RectTransform));
        return child.GetComponent<RectTransform>();
    }

    static RawImage GetOrCreateRawImage(Scene scene, Transform parent, string objectName)
    {
        GameObject imageObject = FindNamedObject(scene, objectName);
        if (imageObject == null)
            imageObject = EnsureChild(parent, objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        else if (imageObject.transform.parent != parent)
        {
            Undo.RecordObject(imageObject.transform, "Arrange DroneFollow monitors");
            imageObject.transform.SetParent(parent, false);
        }

        RawImage image = imageObject.GetComponent<RawImage>();
        if (image == null)
            image = Undo.AddComponent<RawImage>(imageObject);
        return image;
    }

    static void ConfigureScreenImage(RawImage image, RenderTexture texture)
    {
        Undo.RecordObject(image, "Configure DroneFollow monitor image");
        AspectRatioFitter oldAspectFitter = image.GetComponent<AspectRatioFitter>();
        if (oldAspectFitter != null)
            Undo.DestroyObjectImmediate(oldAspectFitter);
        image.texture = texture;
        image.color = Color.white;
        image.raycastTarget = false;
        SetStretch(image.rectTransform, Vector2.zero, Vector2.one);
        image.rectTransform.localScale = Vector3.one;
        EditorUtility.SetDirty(image);
        EditorUtility.SetDirty(image.rectTransform);
    }

    static void SetStretch(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    static void SetPreviewDecorationActive(Scene scene, string objectName, bool active)
    {
        GameObject decoration = FindNamedObject(scene, objectName);
        if (decoration == null)
            return;

        Undo.RecordObject(decoration, "Remove overlapping legacy camera preview");
        decoration.SetActive(active);
        EditorUtility.SetDirty(decoration);
    }

    static void DisableLegacyControlPanel(Scene scene)
    {
        GameObject controlPanel = FindNamedObject(scene, LegacyControlPanelName);
        if (controlPanel == null || !controlPanel.activeSelf)
            return;

        Undo.RecordObject(controlPanel, "Hide legacy DroneFollow control panel");
        controlPanel.SetActive(false);
        EditorUtility.SetDirty(controlPanel);
    }

    static void ConfigureBoundingBoxOverlay(
        Scene scene,
        DroneFollowSentisPerception perception,
        RectTransform mainCameraContent)
    {
        GameObject oldCanvasObject = FindNamedObject(scene, BoundingBoxOverlayCanvasName);
        GameObject overlayObject = FindNamedObject(scene, BoundingBoxOverlayName);
        if (overlayObject == null)
        {
            overlayObject = new GameObject(
                BoundingBoxOverlayName,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(DroneFollowBoundingBoxOverlay));
            Undo.RegisterCreatedObjectUndo(overlayObject, "Create Main Camera detection overlay");
        }

        Undo.RecordObject(overlayObject.transform, "Align detection box to Main Camera screen");
        overlayObject.transform.SetParent(mainCameraContent, false);
        RectTransform overlayRect = overlayObject.GetComponent<RectTransform>();
        SetStretch(overlayRect, Vector2.zero, Vector2.one);
        overlayRect.localScale = Vector3.one;
        overlayObject.transform.SetAsLastSibling();

        DroneFollowBoundingBoxOverlay overlay = overlayObject.GetComponent<DroneFollowBoundingBoxOverlay>();
        if (overlay == null)
            overlay = Undo.AddComponent<DroneFollowBoundingBoxOverlay>(overlayObject);
        Undo.RecordObject(overlay, "Configure Main Camera detection overlay");
        overlay.detector = perception;
        overlay.lineThickness = 2.5f;
        overlay.color = new Color(0.18f, 1f, 0.28f, 1f);
        overlay.raycastTarget = false;

        if (oldCanvasObject != null && oldCanvasObject != mainCameraContent.gameObject)
        {
            Canvas oldCanvas = oldCanvasObject.GetComponent<Canvas>();
            if (oldCanvasObject.transform.childCount == 0)
                Undo.DestroyObjectImmediate(oldCanvasObject);
            else if (oldCanvas != null)
                oldCanvas.enabled = false;
        }

        EditorUtility.SetDirty(overlay);
        EditorUtility.SetDirty(overlayRect);
    }

    static void RecordPrefabInstanceOverride(UnityEngine.Object target)
    {
        if (target != null && PrefabUtility.IsPartOfPrefabInstance(target))
            PrefabUtility.RecordPrefabInstancePropertyModifications(target);
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

    static int RemoveMissingScriptsInScene(Scene scene)
    {
        int removed = 0;
        if (!scene.IsValid() || !scene.isLoaded)
            return removed;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                int removedFromObject = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child.gameObject);
                if (removedFromObject == 0)
                    continue;

                removed += removedFromObject;
                EditorUtility.SetDirty(child.gameObject);
                if (PrefabUtility.IsPartOfPrefabInstance(child.gameObject))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(child.gameObject);
            }
        }

        return removed;
    }

    static int CountMissingScriptsInScene(Scene scene)
    {
        int count = 0;
        if (!scene.IsValid() || !scene.isLoaded)
            return count;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                count += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject);
        }

        return count;
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
