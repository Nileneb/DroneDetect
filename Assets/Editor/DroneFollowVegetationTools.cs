using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class DroneFollowVegetationTools
{
    const string VegetationRootName = "Vegetation";
    const string PrefabFolder = "Assets/Prefabs/DroneFollow/Vegetation";
    const string ObstacleTag = "Obstacle";
    const string SourcePrefabFolder = "Assets/Prefabs/Vegetation";

    /// <summary>Regenerates every optimized vegetation prefab from its source; scene instances keep their links.</summary>
    [MenuItem("DroneDetect/DroneFollow/Rebuild Vegetation Prefabs (Mesh Colliders)")]
    public static void RebuildOptimizedPrefabs()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[DroneFollowVegetation] Stop Play mode before rebuilding vegetation prefabs.");
            return;
        }

        EnsurePrefabFolder();
        int rebuilt = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { SourcePrefabFolder }))
        {
            string sourcePath = AssetDatabase.GUIDToAssetPath(guid);
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (source == null)
            {
                Debug.LogError($"[DroneFollowVegetation] Could not load {sourcePath}.");
                continue;
            }
            if (CreateOptimizedPrefab(source, GetOptimizedPrefabPath(sourcePath)) != null)
                rebuilt++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[DroneFollowVegetation] Rebuilt {rebuilt} vegetation prefabs with mesh colliders in {PrefabFolder}.");
    }

    [MenuItem("DroneDetect/DroneFollow/Optimize Vegetation Prefabs")]
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
            GameObject optimizedPrefab = CreateOptimizedPrefab(source.Value, outputPath);
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

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        Undo.CollapseUndoOperations(undoGroup);

        Debug.Log($"[DroneFollowVegetation] Created {optimizedPrefabs.Count} shared prefabs, replaced {replacedCount} scene objects.");
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

    static GameObject CreateOptimizedPrefab(GameObject sourcePrefab, string outputPath)
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

            prefabRoot.tag = ObstacleTag;
            AddMeshColliders(visual);

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

    // WHY(sim-to-real, collision): the real drone hits branches and rock overhangs, not a capsule around the
    // trunk; the decimated (~30k triangle) meshes are cheap enough to collide against exactly.
    static void AddMeshColliders(GameObject visual)
    {
        foreach (MeshFilter meshFilter in visual.GetComponentsInChildren<MeshFilter>(true))
        {
            if (meshFilter.sharedMesh == null)
                continue;
            MeshCollider meshCollider = meshFilter.GetComponent<MeshCollider>();
            if (meshCollider == null)
                meshCollider = meshFilter.gameObject.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = meshFilter.sharedMesh;
            meshCollider.convex = false;
            meshFilter.gameObject.tag = ObstacleTag;
        }
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
