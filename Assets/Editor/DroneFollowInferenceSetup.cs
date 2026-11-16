using Unity.InferenceEngine;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds the digital-twin inference test scene from the configured training scene: same world, drone and target,
/// but the agent runs the trained policy in deployment mode behind the mission state machine, like on the phone.
/// </summary>
public static class DroneFollowInferenceSetup
{
    public const string TrainingScenePath = "Assets/Scenes/Agent1.unity";
    public const string InferenceScenePath = "Assets/Scenes/DroneFollowInference.unity";

    [MenuItem("DroneDetect/Create Inference Test Scene")]
    static void CreateMenu() => Create();

    public static bool Create()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[InferenceSetup] Stop Play mode first.");
            return false;
        }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return false;

        AssetDatabase.DeleteAsset(InferenceScenePath);
        if (!AssetDatabase.CopyAsset(TrainingScenePath, InferenceScenePath))
        {
            Debug.LogError($"[InferenceSetup] Could not copy {TrainingScenePath}.");
            return false;
        }
        Scene scene = EditorSceneManager.OpenScene(InferenceScenePath, OpenSceneMode.Single);

        var policy = AssetDatabase.LoadAssetAtPath<ModelAsset>(DroneFollowGroundStationSetup.PolicyModelPath);
        if (policy == null)
        {
            Debug.LogError($"[InferenceSetup] Policy missing at {DroneFollowGroundStationSetup.PolicyModelPath}.");
            return false;
        }

        DroneFollowAgent agent = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            agent = root.GetComponentInChildren<DroneFollowAgent>(false);
            if (agent != null)
                break;
        }
        if (agent == null)
        {
            Debug.LogError("[InferenceSetup] No active DroneFollowAgent in Agent1. Run DroneDetect > Configure Active Scene for DroneFollow first.");
            return false;
        }

        GameObject droneObject = agent.gameObject;
        var simulated = droneObject.GetComponent<SimulatedDroneController>();
        var behavior = droneObject.GetComponent<BehaviorParameters>();
        DroneFollowSentisPerception perception = agent.perception;
        if (simulated == null || behavior == null || perception == null || agent.followTarget == null)
        {
            Debug.LogError("[InferenceSetup] Agent1 is incompletely configured (controller, behavior, perception or target missing).");
            return false;
        }

        agent.deploymentMode = true;
        agent.useScriptedExpert = false;
        behavior.BehaviorType = BehaviorType.InferenceOnly;
        behavior.Model = policy;
        perception.randomizeDetections = false;
        perception.autoLockTarget = true;

        Persist(agent);
        Persist(behavior);
        Persist(perception);

        var behaviour = droneObject.GetComponent<DroneBehaviour>() ?? droneObject.AddComponent<DroneBehaviour>();
        behaviour.perception = perception;

        var rigObject = new GameObject("InferenceTestRig");
        var rig = rigObject.AddComponent<InferenceTestRig>();
        rig.behaviour = behaviour;
        rig.drone = simulated;
        rig.perception = perception;
        rig.target = agent.followTarget;
        rig.walker = agent.followTarget.GetComponent<DroneFollowPathWalker>();
        rig.paths = agent.paths;
        if (rig.walker == null)
        {
            Debug.LogError("[InferenceSetup] Target has no DroneFollowPathWalker.");
            return false;
        }

        Persist(behaviour);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, InferenceScenePath);
        Debug.Log($"[InferenceSetup] Saved {InferenceScenePath}: policy '{policy.name}', deployment mode, state machine, scenario rig.");
        return true;
    }

    // Components of prefab instances only keep script-made changes when they are recorded as overrides.
    static void Persist(Object component)
    {
        EditorUtility.SetDirty(component);
        if (PrefabUtility.IsPartOfPrefabInstance(component))
            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
    }

    /// <summary>Batch entry: creates the scene, then runs the scenarios unattended (Play mode) and exits with the result.</summary>
    public static void CreateAndRun()
    {
        if (!Create())
        {
            EditorApplication.Exit(2);
            return;
        }
        EditorApplication.EnterPlaymode();
    }
}
