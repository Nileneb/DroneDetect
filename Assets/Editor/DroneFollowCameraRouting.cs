using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine.UI;

public static class DroneFollowCameraRouting
{
  \[MenuItem("DroneDetect/Configure Active Scene for DroneFollow (camera roles)")\]
  public static void ConfigureCameraRoles()
  {
    if (EditorApplication.isPlayingOrWillChangePlaymode)
    {
      Debug.LogError("\[DroneFollowCameraRouting\] Stop Play mode before configuring camera roles.");
      return;
    }

```
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
Camera depthCamera = FindCamera(drone, "DepthCamera");
if (mainCamera == null || groundCamera == null)
{
  Debug.LogError("[DroneFollowCameraRouting] Required Main Camera or GroundCam is missing. No camera was reassigned.");
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
  Debug.LogError("[DroneFollowCameraRouting] GroundCam has no PiP_Ground RenderTexture; it was kept out of the main display.");

bool depthPreviewReady = false;
if (depthCamera != null)
{
  depthCamera.gameObject.tag = "Untagged";
  depthPreviewReady = RoutePreview(scene, depthCamera, "PiP_Depth", false);
}

foreach (Camera camera in drone.GetComponentsInChildren&lt;Camera&gt;(true))
{
  if (camera == null || camera == mainCamera || camera == groundCamera || camera == depthCamera)
    continue;

  Undo.RecordObject(camera, "Keep secondary camera off primary display");
  camera.targetDisplay = 0;
  if (camera.targetTexture == null)
    camera.enabled = false;
  EditorUtility.SetDirty(camera);
}

var detector = drone.GetComponent&lt;SimulatedBoundingBoxDetector&gt;();
if (detector != null)
{
  Undo.RecordObject(detector, "Use GroundCam for target detection");
  detector.observationCamera = groundCamera;
  EditorUtility.SetDirty(detector);
}
else
{
  Debug.LogError("[DroneFollowCameraRouting] SimulatedBoundingBoxDetector is missing.");
}

var behavior = drone.GetComponent&lt;BehaviorParameters&gt;();
if (behavior != null)
{
  Undo.RecordObject(behavior, "Disable child camera sensors for DroneFollow");
  behavior.UseChildSensors = false;
  EditorUtility.SetDirty(behavior);
}

int removedSensors = RemoveCameraSensors(scene);
EditorSceneManager.MarkSceneDirty(scene);
EditorSceneManager.SaveOpenScenes();
Debug.Log("[DroneFollowCameraRouting] Camera roles saved: Main Camera -> primary display; GroundCam -> PiP_Ground and detector; DepthCamera -> PiP_Depth only. Ground preview=" + groundPreviewReady + ", depth preview=" + depthPreviewReady + ", removed CameraSensorComponent(s)=" + removedSensors + ". Camera rotations were not changed.");
```

  }

  static bool RoutePreview(Scene scene, Camera camera, string previewName, bool required)
  {
    GameObject previewObject = FindNamedObject(scene, previewName);
    RawImage preview = previewObject != null ? previewObject.GetComponent<RawImage>() : null;
    RenderTexture texture = preview != null ? preview.texture as RenderTexture : null;

```
Undo.RecordObject(camera, "Route " + camera.name + " to " + previewName);
camera.targetDisplay = 0;
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
```

  }

  static int RemoveCameraSensors(Scene scene)
  {
    int removed = 0;
    if (!scene.IsValid() || !scene.isLoaded)
      return removed;

```
foreach (GameObject root in scene.GetRootGameObjects())
{
  CameraSensorComponent[] sensors = root.GetComponentsInChildren&lt;CameraSensorComponent&gt;(true);
  foreach (CameraSensorComponent sensor in sensors)
  {
    if (sensor == null)
      continue;
    Undo.DestroyObjectImmediate(sensor);
    removed++;
  }
}
return removed;
```

  }

  static DroneFollowAgent FindAgent(Scene scene)
  {
    if (!scene.IsValid() || !scene.isLoaded)
      return null;

```
foreach (GameObject root in scene.GetRootGameObjects())
{
  DroneFollowAgent[] agents = root.GetComponentsInChildren&lt;DroneFollowAgent&gt;(true);
  foreach (DroneFollowAgent agent in agents)
  {
    if (agent != null && agent.gameObject.activeInHierarchy)
      return agent;
  }
}
return null;
```

  }

  static Camera FindCamera(GameObject root, string cameraName)
  {
    foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
    {
      if (camera != null &amp;&amp; camera.name == cameraName)
        return camera;
    }
    return null;
  }

  static GameObject FindNamedObject(Scene scene, string objectName)
  {
    if (!scene.IsValid() || !scene.isLoaded)
      return null;

```
foreach (GameObject root in scene.GetRootGameObjects())
{
  if (root.name == objectName)
    return root;
  foreach (Transform child in root.GetComponentsInChildren&lt;Transform&gt;(true))
  {
    if (child.name == objectName)
      return child.gameObject;
  }
}
return null;
```

  }
}