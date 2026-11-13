using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Policies;

/// <summary>
/// One-click wiring for the existing Agent1 training scene. It keeps DroneCSI
/// on the component, disables it for this scene, and enables DroneFollow with
/// a projected moving-target proxy and no child pixel sensors.
/// </summary>
public static class DroneFollowSetup
{
    [MenuItem("DroneDetect/Configure Active Scene for DroneFollow")]
    public static void ConfigureActiveScene()
    {
        var simulatedDrone = Object.FindAnyObjectByType<SimulatedDroneController>();
        if (simulatedDrone == null)
        {
            Debug.LogError("[DroneFollowSetup] No active SimulatedDroneController found. Open Assets/Scenes/Agent1.unity first.");
            return;
        }

        var legacyAgent = simulatedDrone.GetComponent<DroneAgent>();
        if (legacyAgent == null)
        {
            Debug.LogError("[DroneFollowSetup] DroneAgent with the existing target list was not found on the drone.");
            return;
        }

        Transform target = null;
        if (legacyAgent.targets != null)
        {
            foreach (GameObject candidate in legacyAgent.targets)
            {
                if (candidate != null)
                {
                    target = candidate.transform;
                    break;
                }
            }
        }
        if (target == null)
        {
            Debug.LogError("[DroneFollowSetup] No non-null target is assigned in DroneAgent.targets.");
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
        Undo.RecordObject(droneObject, "Configure DroneFollow");

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
        detector.sampleRateHz = 10f;
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
        behavior.BehaviorName = "DroneFollow";
        behavior.BehaviorType = BehaviorType.Default;
        behavior.UseChildSensors = false; // Excludes the existing depth RenderTexture sensor.

        var requester = droneObject.GetComponent<DecisionRequester>();
        if (requester == null)
            requester = Undo.AddComponent<DecisionRequester>(droneObject);
        Undo.RecordObject(requester, "Set DroneFollow decision rate");
        requester.DecisionPeriod = 5; // 10 Hz at the project's 50 Hz fixed-step rate.
        requester.TakeActionsBetweenDecisions = true;

        EditorUtility.SetDirty(behavior);
        EditorUtility.SetDirty(detector);
        EditorUtility.SetDirty(targetMotion);
        EditorUtility.SetDirty(followAgent);
        EditorUtility.SetDirty(requester);
        EditorSceneManager.MarkSceneDirty(simulatedDrone.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();
        Selection.activeGameObject = droneObject;

        Debug.Log("[DroneFollowSetup] Agent1 is configured for DroneFollow. Existing waypoint sphere is the moving projected proxy; replace it with a humanoid/GLB later.");
    }
}
