#if false

```csharp
#endif
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine.UI;

public static class DroneFollowCameraRouting
{
    [MenuItem("DroneDetect/Configure Active Scene for DroneFollow (camera roles)")]
    public static void ConfigureCameraRoles()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[DroneFollowCameraRouting] Stop Play mode before configuring camera roles.");
            return;
        }
        DroneFollowSetup.ConfigureActiveScene();
        Scene scene = SceneManager.GetActiveScene();
        DroneFollowAgent agent = FindAgent(scene);
        if (agent == null)
        {
            Debug.LogError("[DroneFollowCameraRouting] No DroneFollowAgent found in the active scene.");
            return;
        }
        GameObject drone = agent.gameObject;
        Camera mainCamera = FindCamera(drone, "Main Camera");
        Camera groundCamera = FindCamera(drone, "GroundCam");
        if (mainCamera == null || groundCamera == null)
        {
            Debug.LogError("[DroneFollowCameraRouting] Main Camera and GroundCam are required. No camera roles were reassigned.");
            return;
        }
        Undo.RecordObject(mainCamera, "Route Main Camera to primary display");
        mainCamera.targetTexture = null;
        mainCamera.targetDisplay = 0;
        mainCamera.rect = new Rect(0f, 0f, 1f, 1f);
        mainCamera.enabled = true;
        mainCamera.gameObject.tag = "MainCamera";
        EditorUtility.SetDirty(mainCamera);
        groundCamera.gameObject.tag = "Untagged";
        bool groundPreviewReady = RoutePreview(scene, groundCamera, "PiP_Ground", true);
        if (!groundPreviewReady)
            Debug.LogError("[DroneFollowCameraRouting] GroundCam has no PiP_Ground RenderTexture; it will not render into the main display.");
        foreach (Camera camera in drone.GetComponentsInChildren<Camera>(true))
        {
            if (camera == null || camera == mainCamera || camera == groundCamera)
                continue;
            Undo.RecordObject(camera, "Keep secondary camera off primary display");
            camera.targetDisplay = 0;
            camera.enabled = false;
            EditorUtility.SetDirty(camera);
        }
        SimulatedBoundingBoxDetector detector = drone.GetComponent<SimulatedBoundingBoxDetector>();
        if (detector != null)
        {
            Undo.RecordObject(detector, "Use Main Camera for target detection");
            detector.observationCamera = mainCamera;
            EditorUtility.SetDirty(detector);
        }
        else
        {
            Debug.LogError("[DroneFollowCameraRouting] SimulatedBoundingBoxDetector is missing.");
        }
        BehaviorParameters behavior = drone.GetComponent<BehaviorParameters>();
        if (behavior != null)
        {
            Undo.RecordObject(behavior, "Disable child camera sensors for DroneFollow");
            behavior.UseChildSensors = false;
            EditorUtility.SetDirty(behavior);
        }
        int removedSensors = RemoveCameraSensors(scene);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[DroneFollowCameraRouting] Saved roles: Main Camera -> primary RGB display, target detection, centered depth preview and policy observation; downward GroundCam -> PiP_Ground; no separate depth camera. Ground preview=" + groundPreviewReady + ", removed CameraSensorComponent(s)=" + removedSensors + ".");
    }
    static bool RoutePreview(Scene scene, Camera camera, string previewName, bool required)
    {
        GameObject previewObject = FindNamedObject(scene, previewName);
        RawImage preview = previewObject != null ? previewObject.GetComponent<RawImage>() : null;
        RenderTexture texture = preview != null ? preview.texture as RenderTexture : null;
        Undo.RecordObject(camera, "Route " + camera.name + " to " + previewName);
        camera.targetDisplay = 0;
        camera.rect = new Rect(0f, 0f, 1f, 1f);
        if (texture != null)
        {
            camera.targetTexture = texture;
            camera.enabled = true;
            EditorUtility.SetDirty(camera);
            return true;
        }
        if (!required && camera.targetTexture != null)
        {
            camera.enabled = true;
            EditorUtility.SetDirty(camera);
            Debug.LogWarning("[DroneFollowCameraRouting] " + previewName + " is missing; keeping " + camera.name + " on its existing RenderTexture.");
            return true;
        }
        camera.enabled = false;
        EditorUtility.SetDirty(camera);
        Debug.LogWarning("[DroneFollowCameraRouting] Could not route " + camera.name + " to " + previewName + ".");
        return false;
    }
    static int RemoveCameraSensors(Scene scene)
    {
        int removed = 0;
        if (!scene.IsValid() || !scene.isLoaded)
            return removed;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            CameraSensorComponent[] sensors = root.GetComponentsInChildren<CameraSensorComponent>(true);
            foreach (CameraSensorComponent sensor in sensors)
            {
                if (sensor == null)
                    continue;
                Undo.DestroyObjectImmediate(sensor);
                removed++;
            }
        }
        return removed;
    }
    static DroneFollowAgent FindAgent(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            DroneFollowAgent[] agents = root.GetComponentsInChildren<DroneFollowAgent>(true);
            foreach (DroneFollowAgent agent in agents)
            {
                if (agent != null && agent.gameObject.activeInHierarchy)
                    return agent;
            }
        }
        return null;
    }
    static Camera FindCamera(GameObject root, string cameraName)
    {
        foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
        {
            if (camera != null && camera.name == cameraName)
                return camera;
        }
        return null;
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
}
#if false
```

#endif