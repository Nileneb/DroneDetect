using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;

/// <summary>
/// One-click wiring for the existing Agent1 training scene. It keeps DroneCSI
/// on the component, disables it for this scene, and enables DroneFollow with
/// a projected animated PrefabBene target and no child pixel sensors.
/// </summary>
public static class DroneFollowSetup
{
    const string TrainingScenePath = "Assets/Scenes/Agent1.unity";
    const string TargetPrefabPath = "Assets/Prefabs/PrefabBene.prefab";
    const string TargetInstanceName = "DroneFollowTarget";

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
            Debug.LogError($"[DroneFollowSetup] No SimulatedDroneController was found in {TrainingScenePath}. Check the scene and drone prefab.");
            return;
        }
        if (!simulatedDrone.gameObject.activeInHierarchy)
        {
            Debug.LogError("[DroneFollowSetup] The drone controller exists in Agent1 but its GameObject is inactive. Activate the drone and retry.");
            return;
        }

        var legacyAgent = simulatedDrone.GetComponent<DroneAgent>();
        if (legacyAgent == null)
        {
            Debug.LogError("[DroneFollowSetup] DroneAgent with the existing target list was not found on the drone.");
            return;
        }

        Camera camera = legacyAgent.depthCamera != null
            ? legacyAgent.depthCamera.GetComponent<Camera>()
            : simulatedDrone.GetComponentInChildren<Camera>(true);
        if (camera == null)
        {
            Debug.LogError("[DroneFollowSetup] No camera found on the drone or its children.");
            return;
        }

        GameObject droneObject = simulatedDrone.gameObject;
        Scene trainingScene = droneObject.scene;
        Undo.RecordObject(droneObject, "Configure DroneFollow");

        // Use the animated character prefab rather than a waypoint sphere. Reuse
        // the named instance when the setup menu is run a second time.
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

        // The old DroneCSI waypoint markers are not part of the new task.
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
        behavior.UseChildSensors = false; // Excludes the existing depth RenderTexture sensor.

        // BrainParameters is a read-only property, but returns the mutable
        // reference object. Mutate that object in place to align its spec with
        // CollectObservations and OnActionReceived.
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

        Debug.Log($"[DroneFollowSetup] Agent1 configured with {TargetPrefabPath}: {DroneFollowContract.ObservationSize} observations, {DroneFollowContract.ActionSize} continuous actions, {actualRateHz:F1} Hz. No child pixel sensors.");
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
            var controller = root.GetComponentInChildren<SimulatedDroneController>(true);
            if (controller != null)
                return controller;
        }

        return null;
    }
}
