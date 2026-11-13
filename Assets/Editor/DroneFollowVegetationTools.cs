using System;
using System.Collections.Generic;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

public static class DroneFollowVegetationTools
{
    const string VegetationRootName = "Vegetation";
    const string NavMeshRootName = "DroneFollowNavMesh";
    const string NavMeshLayerName = "NavMeshGeometry";
    const string PrefabFolder = "Assets/Prefabs/DroneFollow/Vegetation";
    [MenuItem("DroneDetect/DroneFollow/Optimize Vegetation Prefabs and Bake NavMesh")]
    public static void OptimizeActiveScene()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[DroneFollowVegetation] Stop Play mode before optimizing vegetation.");
            return;
        }

        GameObject vegetationRoot = GameObject.Find(VegetationRootName);
        GameObject ground = FindByTag("Ground");
        if (vegetationRoot == null || ground == null || ground.GetComponent<Collider>() == null)
        {
            Debug.LogError("[DroneFollowVegetation] The active scene must contain a Vegetation root and a Ground-tagged object with a Collider.");
            return;
        }

        int navMeshLayer = EnsureNavMeshLayer();
        if (navMeshLayer < 0)
            return;

        List<GameObject> vegetation = FindVegetationWithMeshColliders(vegetationRoot.transform);
        if (vegetation.Count == 0)
        {
            Debug.LogError("[DroneFollowVegetation] No unoptimized vegetation MeshColliders were found under the Vegetation root.");
            return;
        }

        var sourceByInstance = new Dictionary<GameObject, string>();
        var sourceByPath = new Dictionary<string, GameObject>();
        foreach (GameObject instance in vegetation)
        {
            string sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance);
            GameObject sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (string.IsNullOrEmpty(sourcePath) || sourcePrefab == null)
            {
                Debug.LogError($"[DroneFollowVegetation] {instance.name} is not a prefab instance with a loadable source asset.");
                return;
            }

            sourceByInstance.Add(instance, sourcePath);
            sourceByPath[sourcePath] = sourcePrefab;
        }

        EnsurePrefabFolder();
        var optimizedPrefabs = new Dictionary<string, GameObject>();
        foreach (KeyValuePair<string, GameObject> source in sourceByPath)
        {
            string outputPath = GetOptimizedPrefabPath(source.Key);
            GameObject optimizedPrefab = CreateOptimizedPrefab(source.Value, outputPath, navMeshLayer);
            if (optimizedPrefab == null)
                return;
            optimizedPrefabs.Add(source.Key, optimizedPrefab);
        }

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Optimize DroneFollow vegetation");

        int replacedCount = 0;
        foreach (GameObject instance in vegetation)
        {
            if (instance == null) continue;

            Transform oldTransform = instance.transform;
            Transform parent = oldTransform.parent;
            Vector3 localPosition = oldTransform.localPosition;
            Quaternion localRotation = oldTransform.localRotation;
            Vector3 localScale = oldTransform.localScale;
            bool active = instance.activeSelf;
            string objectName = instance.name;
            string tag = instance.tag;
            StaticEditorFlags staticFlags = GameObjectUtility.GetStaticEditorFlags(instance);
            GameObject prefab = optimizedPrefabs[sourceByInstance[instance]];

            GameObject replacement = PrefabUtility.InstantiatePrefab(prefab, parent) as GameObject;
            if (replacement == null)
            {
                Debug.LogError($"[DroneFollowVegetation] Could not instantiate {prefab.name} for {objectName}.");
                Undo.RevertAllDownToGroup(undoGroup);
                return;
            }

            Undo.RegisterCreatedObjectUndo(replacement, "Replace vegetation with optimized prefab");
            replacement.name = objectName;
            replacement.transform.SetLocalPositionAndRotation(localPosition, localRotation);
            replacement.transform.localScale = localScale;
            replacement.tag = tag;
            replacement.SetActive(active);
            GameObjectUtility.SetStaticEditorFlags(replacement, staticFlags);
            Undo.DestroyObjectImmediate(instance);
            replacedCount++;
        }

        Undo.RecordObject(ground, "Assign NavMesh geometry layer to Ground");
        ground.layer = navMeshLayer;
        ConfigureAndBakeNavMesh(navMeshLayer);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        Undo.CollapseUndoOperations(undoGroup);

        Debug.Log($"[DroneFollowVegetation] Created {optimizedPrefabs.Count} shared prefabs, replaced {replacedCount} scene objects, assigned {NavMeshLayerName}, and baked the NavMesh.");
    }

    static int EnsureNavMeshLayer()
    {
        UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (assets.Length == 0)
        {
            Debug.LogError("[DroneFollowVegetation] Could not load ProjectSettings/TagManager.asset.");
            return -1;
        }

        var tagManager = new SerializedObject(assets[0]);
        SerializedProperty layers = tagManager.FindProperty("layers");
        if (layers == null)
        {
            Debug.LogError("[DroneFollowVegetation] The TagManager layers property is unavailable.");
            return -1;
        }

        int freeLayer = -1;
        for (int i = 8; i < layers.arraySize; i++)
        {
            SerializedProperty layer = layers.GetArrayElementAtIndex(i);
            if (layer.stringValue == NavMeshLayerName)
                return i;
            if (freeLayer < 0 && string.IsNullOrEmpty(layer.stringValue))
                freeLayer = i;
        }

        if (freeLayer < 0)
        {
            Debug.LogError($"[DroneFollowVegetation] No free user layer is available for {NavMeshLayerName}.");
            return -1;
        }

        layers.GetArrayElementAtIndex(freeLayer).stringValue = NavMeshLayerName;
        tagManager.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        return freeLayer;
    }

    static List<GameObject> FindVegetationWithMeshColliders(Transform root)
    {
        var results = new List<GameObject>();
        for (int i = 0; i < root.childCount; i++)
        {
            GameObject child = root.GetChild(i).gameObject;
            string sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(child);
            if (!string.IsNullOrEmpty(sourcePath) &&
                sourcePath.StartsWith(PrefabFolder + "/", StringComparison.Ordinal))
                continue;

            if (child.GetComponentsInChildren<MeshCollider>(true).Length > 0)
                results.Add(child);
        }

        return results;
    }

    static GameObject CreateOptimizedPrefab(GameObject sourcePrefab, string outputPath, int navMeshLayer)
    {
        var prefabRoot = new GameObject(Path.GetFileNameWithoutExtension(outputPath));
        try
        {
            GameObject visual = PrefabUtility.InstantiatePrefab(sourcePrefab) as GameObject;
            if (visual == null)
            {
                Debug.LogError($"[DroneFollowVegetation] Could not instantiate source prefab {AssetDatabase.GetAssetPath(sourcePrefab)}.");
                return null;
            }

            visual.transform.SetParent(prefabRoot.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;

            if (!TryGetLocalRendererBounds(prefabRoot.transform, visual, out Bounds bounds))
            {
                Debug.LogError($"[DroneFollowVegetation] Source prefab {sourcePrefab.name} has no renderable mesh bounds.");
                return null;
            }

            prefabRoot.tag = "Obstacle";
            CreateCollisionProxy(prefabRoot.transform, bounds, navMeshLayer);

            GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(prefabRoot, outputPath);
            if (savedPrefab == null)
            {
                Debug.LogError($"[DroneFollowVegetation] Failed to save optimized prefab to {outputPath}.");
                return null;
            }

            return savedPrefab;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(prefabRoot);
        }
    }

    static bool TryGetLocalRendererBounds(Transform root, GameObject visual, out Bounds bounds)
    {
        Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
        bool hasBounds = false;
        bounds = default;

        foreach (Renderer renderer in renderers)
        {
            Bounds worldBounds = renderer.bounds;
            Vector3 center = worldBounds.center;
            Vector3 extents = worldBounds.extents;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 worldPoint = center + Vector3.Scale(extents, new Vector3(
                    (corner & 1) == 0 ? -1f : 1f,
                    (corner & 2) == 0 ? -1f : 1f,
                    (corner & 4) == 0 ? -1f : 1f));
                Vector3 localPoint = root.InverseTransformPoint(worldPoint);
                if (!hasBounds)
                {
                    bounds = new Bounds(localPoint, Vector3.zero);
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(localPoint);
                }
            }
        }

        return hasBounds;
    }

    static void CreateCollisionProxy(Transform prefabRoot, Bounds bounds, int navMeshLayer)
    {
        var proxy = new GameObject("CollisionProxy");
        proxy.layer = navMeshLayer;
        proxy.tag = "Obstacle";
        proxy.transform.SetParent(prefabRoot, false);

        Vector3 size = bounds.size;
        float horizontalSize = Mathf.Max(size.x, size.z);
        bool tallObject = size.y >= horizontalSize * 1.2f;
        if (tallObject)
        {
            float radius = Mathf.Clamp(horizontalSize * 0.1f, 0.15f, 0.6f);
            float height = Mathf.Max(size.y * 0.92f, radius * 2f);
            var collider = proxy.AddComponent<CapsuleCollider>();
            collider.direction = 1;
            collider.radius = radius;
            collider.height = height;
            collider.center = new Vector3(bounds.center.x, bounds.min.y + height * 0.5f, bounds.center.z);
        }
        else
        {
            var collider = proxy.AddComponent<BoxCollider>();
            collider.center = bounds.center;
            collider.size = new Vector3(
                Mathf.Max(size.x, 0.2f),
                Mathf.Max(size.y, 0.2f),
                Mathf.Max(size.z, 0.2f));
        }
    }

    static void ConfigureAndBakeNavMesh(int navMeshLayer)
    {
        GameObject surfaceObject = GameObject.Find(NavMeshRootName);
        if (surfaceObject == null)
        {
            surfaceObject = new GameObject(NavMeshRootName);
            Undo.RegisterCreatedObjectUndo(surfaceObject, "Create DroneFollow NavMesh surface");
        }

        NavMeshSurface surface = surfaceObject.GetComponent<NavMeshSurface>();
        if (surface == null)
            surface = Undo.AddComponent<NavMeshSurface>(surfaceObject);

        surface.agentTypeID = 0;
        surface.collectObjects = CollectObjects.All;
        surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
        surface.layerMask = 1 << navMeshLayer;
        surface.BuildNavMesh();
        if (surface.navMeshData == null)
            Debug.LogError("[DroneFollowVegetation] NavMesh build returned no data. Check that the Ground collider and vegetation proxies use the NavMeshGeometry layer.");
    }

    static GameObject FindByTag(string tag)
    {
        try
        {
            return GameObject.FindGameObjectWithTag(tag);
        }
        catch (UnityException)
        {
            return null;
        }
    }

    static string GetOptimizedPrefabPath(string sourcePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(sourcePath);
        return $"{PrefabFolder}/{fileName}_Optimized.prefab";
    }

    static void EnsurePrefabFolder()
    {
        EnsureAssetFolder("Assets/Prefabs/DroneFollow");
        EnsureAssetFolder(PrefabFolder);
    }

    static void EnsureAssetFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder))
            return;

        int separator = folder.LastIndexOf('/');
        string parent = folder.Substring(0, separator);
        string name = folder.Substring(separator + 1);
        EnsureAssetFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }
}
