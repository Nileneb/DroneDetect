using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds the DroneFollow training world: hilly terrain, start/landing pad, four walking loops of
/// increasing difficulty, scattered rocks and trees beside the paths, and the gate triggers.
/// Paths are ordinary scene objects afterwards: move their waypoints by hand, then run
/// "Rebuild Path Gates".
/// </summary>
public static class DroneFollowTerrainBuilder
{
    const string WorldRootName = "DroneFollowWorld";
    const string PathRootName = "Paths";
    const string SceneryRootName = "Scenery";
    const string LegacyVegetationRootName = "Vegetation";
    const string GroundTag = "Ground";
    const string AssetFolder = "Assets/Environment/DroneFollowTerrain";
    const string VegetationPrefabFolder = "Assets/Prefabs/DroneFollow/Vegetation";
    const string TextureFolder = "Assets/Realistic Drone/Terrain/city/city";

    const float TerrainSize = 200f;
    const float TerrainHeight = 10f;
    const float MaxHillHeight = 8f;
    const int HeightmapResolution = 257;
    const float FlatPadRadius = 12f;
    const float FirstWaypointDistance = 4f;
    const float WaypointSpacing = 12f;
    const float PathClearance = 5f;
    const float PadClearance = 12f;
    const int SceneryCount = 220;
    const float NearPathShare = 0.4f;
    const int Seed = 20261115;

    public const float GateWidth = 8f;
    public const float GateHeight = 4f;
    public const float GateDepth = 0.3f;

    // WHY(sim-to-real, perception): this Meshy rock contains a human figure that YOLO detects as a
    // second person; it must never be scattered.
    static readonly string[] ExcludedPrefabFragments = { "Balanced_Rock" };

    // Per path: hill scale in its quadrant, loop radius, wobble amplitude and lobe count (tight turns).
    static readonly (float hills, float radius, float wobble, int lobes)[] PathShapes =
    {
        (0.1f, 21f, 0.10f, 2),
        (0.4f, 24f, 0.15f, 3),
        (0.7f, 27f, 0.22f, 3),
        (1.0f, 28f, 0.30f, 4),
    };

    [MenuItem("DroneDetect/DroneFollow/Build Terrain, Paths and Gates")]
    static void BuildWorldMenu() => BuildWorld();

    [MenuItem("DroneDetect/DroneFollow/Rebuild Path Gates")]
    static void RebuildGatesMenu()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[DroneFollowTerrain] Stop Play mode before rebuilding gates.");
            return;
        }

        DroneFollowPath[] paths = Object.FindObjectsByType<DroneFollowPath>(FindObjectsInactive.Include);
        if (paths.Length == 0)
        {
            Debug.LogError("[DroneFollowTerrain] No DroneFollowPath in the open scene. Run Build Terrain, Paths and Gates first.");
            return;
        }

        int gateCount = 0;
        foreach (DroneFollowPath path in paths)
            gateCount += RebuildGates(path);
        EditorSceneManager.MarkSceneDirty(paths[0].gameObject.scene);
        Debug.Log($"[DroneFollowTerrain] Snapped waypoints to the ground and rebuilt {gateCount} gates on {paths.Length} paths.");
    }

    public static bool BuildWorld()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[DroneFollowTerrain] Stop Play mode before building the world.");
            return false;
        }

        GameObject[] vegetationPrefabs = LoadVegetationPrefabs();
        if (vegetationPrefabs.Length == 0)
        {
            Debug.LogError($"[DroneFollowTerrain] No optimized vegetation prefabs in {VegetationPrefabFolder}. Run DroneDetect > DroneFollow > Optimize Vegetation Prefabs first.");
            return false;
        }

        GameObject existing = GameObject.Find(WorldRootName);
        if (existing != null)
            Undo.DestroyObjectImmediate(existing);
        int disabledLegacy = DisableLegacyGround();

        var world = new GameObject(WorldRootName);
        Undo.RegisterCreatedObjectUndo(world, "Build DroneFollow world");
        Vector3 padCenter = Vector3.zero;

        var shapes = new List<Vector3[]>();
        for (int i = 0; i < PathShapes.Length; i++)
            shapes.Add(LoopWaypoints(padCenter, i));

        Terrain terrain = CreateTerrain(world.transform, padCenter);
        if (terrain == null)
            return false;
        Physics.SyncTransforms();

        GameObject pad = CreatePad(world.transform, terrain, padCenter);
        var pathRoot = new GameObject(PathRootName).transform;
        pathRoot.SetParent(world.transform, false);
        var paths = new List<DroneFollowPath>();
        int gateCount = 0;
        for (int i = 0; i < shapes.Count; i++)
        {
            DroneFollowPath path = CreatePath(pathRoot, i, shapes[i]);
            gateCount += RebuildGates(path);
            paths.Add(path);
        }

        int sceneryPlaced = ScatterScenery(world.transform, terrain, vegetationPrefabs, shapes, padCenter);
        EditorSceneManager.MarkSceneDirty(world.scene);
        Debug.Log(
            $"[DroneFollowTerrain] Built {TerrainSize:F0}x{TerrainSize:F0} m terrain, pad '{pad.name}', {paths.Count} paths " +
            $"({string.Join(", ", paths.Select(p => $"{p.name}: {p.Length:F0} m"))}), {gateCount} gates, " +
            $"{sceneryPlaced} scenery objects; disabled {disabledLegacy} legacy ground/wall/vegetation object(s). " +
            "Now run DroneDetect > Configure Active Scene for DroneFollow.");
        return true;
    }

    /// <summary>Pad position the drone spawns on (top surface centre), or null if the world was not built.</summary>
    public static Transform FindPad() => GameObject.Find($"{WorldRootName}/LandingPad")?.transform;

    public static DroneFollowPath[] FindPaths() =>
        Object.FindObjectsByType<DroneFollowPath>(FindObjectsInactive.Exclude)
            .OrderBy(path => path.difficulty).ThenBy(path => path.name).ToArray();

    static int DisableLegacyGround()
    {
        int disabled = 0;
        foreach (GameObject candidate in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude))
        {
            bool legacyGround = candidate.CompareTag(GroundTag) && candidate.GetComponent<Terrain>() == null;
            bool wall = candidate.CompareTag("Wall");
            bool legacyVegetation = candidate.name == LegacyVegetationRootName && candidate.transform.parent == null;
            if (!legacyGround && !wall && !legacyVegetation)
                continue;
            Undo.RecordObject(candidate, "Disable legacy DroneFollow ground");
            candidate.SetActive(false);
            disabled++;
        }
        return disabled;
    }

    static Vector3 Direction(int pathIndex)
    {
        float angle = pathIndex * Mathf.PI * 0.5f;
        return new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
    }

    /// <summary>Closed loop starting 4 m from the pad, wobbling around a circle in this path's quadrant.</summary>
    static Vector3[] LoopWaypoints(Vector3 pad, int pathIndex)
    {
        (float _, float radius, float wobble, int lobes) = PathShapes[pathIndex];
        Vector3 direction = Direction(pathIndex);
        Vector3 start = pad + direction * FirstWaypointDistance;
        Vector3 center = start + direction * radius;
        int count = Mathf.Max(8, Mathf.RoundToInt(2f * Mathf.PI * radius / WaypointSpacing));
        float startAngle = Mathf.Atan2(-direction.x, -direction.z);
        var points = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)count * Mathf.PI * 2f;
            // sin(lobes * t) is 0 at t = 0, so waypoint 0 stays exactly at the start next to the pad.
            float r = radius * (1f + wobble * Mathf.Sin(lobes * t));
            float angle = startAngle + t;
            points[i] = center + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * r;
        }
        return points;
    }

    static float HillScale(Vector3 worldPoint, Vector3 pad)
    {
        Vector3 offset = worldPoint - pad;
        float distance = new Vector2(offset.x, offset.z).magnitude;
        float flat = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(FlatPadRadius, FlatPadRadius + 15f, distance));
        float scale = 0f;
        float weightSum = 0f;
        for (int i = 0; i < PathShapes.Length; i++)
        {
            float alignment = Vector3.Dot(offset.normalized, Direction(i));
            float weight = Mathf.Pow(Mathf.Max(0f, alignment) + 0.05f, 4f);
            scale += PathShapes[i].hills * weight;
            weightSum += weight;
        }
        return flat * (weightSum > 0f ? scale / weightSum : 0f);
    }

    static Terrain CreateTerrain(Transform parent, Vector3 pad)
    {
        EnsureFolder(AssetFolder);
        var data = new TerrainData { heightmapResolution = HeightmapResolution };
        data.size = new Vector3(TerrainSize, TerrainHeight, TerrainSize);
        Vector3 origin = pad - new Vector3(TerrainSize * 0.5f, 0f, TerrainSize * 0.5f);

        var heights = new float[HeightmapResolution, HeightmapResolution];
        for (int z = 0; z < HeightmapResolution; z++)
        for (int x = 0; x < HeightmapResolution; x++)
        {
            Vector3 world = origin + new Vector3(
                x / (float)(HeightmapResolution - 1) * TerrainSize, 0f,
                z / (float)(HeightmapResolution - 1) * TerrainSize);
            float noise = 0.7f * Mathf.PerlinNoise(world.x / 60f + 31.7f, world.z / 60f + 11.3f) +
                          0.3f * Mathf.PerlinNoise(world.x / 23f + 5.1f, world.z / 23f + 71.9f);
            heights[z, x] = HillScale(world, pad) * MaxHillHeight * noise / TerrainHeight;
        }
        data.SetHeights(0, 0, heights);

        TerrainLayer dark = CreateLayer("Grass_Dark_Green");
        TerrainLayer light = CreateLayer("Grass_Light_Green");
        if (dark == null || light == null)
            return null;
        data.terrainLayers = new[] { dark, light };
        data.alphamapResolution = 256;
        var alpha = new float[256, 256, 2];
        for (int z = 0; z < 256; z++)
        for (int x = 0; x < 256; x++)
        {
            float blend = Mathf.PerlinNoise(x / 18f + 3.3f, z / 18f + 9.1f);
            alpha[z, x, 0] = blend;
            alpha[z, x, 1] = 1f - blend;
        }
        data.SetAlphamaps(0, 0, alpha);

        string dataPath = $"{AssetFolder}/DroneFollowTerrainData.asset";
        AssetDatabase.DeleteAsset(dataPath);
        AssetDatabase.CreateAsset(data, dataPath);

        GameObject terrainObject = Terrain.CreateTerrainGameObject(data);
        Undo.RegisterCreatedObjectUndo(terrainObject, "Create DroneFollow terrain");
        terrainObject.name = "Terrain";
        terrainObject.tag = GroundTag;
        terrainObject.transform.SetParent(parent, false);
        terrainObject.transform.position = origin;
        GameObjectUtility.SetStaticEditorFlags(terrainObject, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
        return terrainObject.GetComponent<Terrain>();
    }

    static TerrainLayer CreateLayer(string textureName)
    {
        string texturePath = $"{TextureFolder}/{textureName}.jpg";
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        if (texture == null)
        {
            Debug.LogError($"[DroneFollowTerrain] Terrain texture {texturePath} not found.");
            return null;
        }

        string layerPath = $"{AssetFolder}/{textureName}.terrainlayer";
        var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerPath);
        if (layer == null)
        {
            layer = new TerrainLayer();
            AssetDatabase.CreateAsset(layer, layerPath);
        }
        layer.diffuseTexture = texture;
        layer.tileSize = new Vector2(4f, 4f);
        EditorUtility.SetDirty(layer);
        return layer;
    }

    static GameObject CreatePad(Transform parent, Terrain terrain, Vector3 padCenter)
    {
        GameObject pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(pad, "Create landing pad");
        pad.name = "LandingPad";
        pad.tag = GroundTag;
        pad.transform.SetParent(parent, false);
        pad.transform.localScale = new Vector3(1.5f, 0.1f, 1.5f);
        float groundY = terrain.SampleHeight(padCenter) + terrain.transform.position.y;
        pad.transform.position = new Vector3(padCenter.x, groundY + 0.05f, padCenter.z);
        var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Obstacles.mat");
        if (material != null)
            pad.GetComponent<Renderer>().sharedMaterial = material;
        return pad;
    }

    static DroneFollowPath CreatePath(Transform parent, int index, Vector3[] points)
    {
        var pathObject = new GameObject($"Path{index}_Difficulty{index}");
        Undo.RegisterCreatedObjectUndo(pathObject, "Create DroneFollow path");
        pathObject.transform.SetParent(parent, false);
        var waypointRoot = new GameObject(DroneFollowPath.WaypointRootName).transform;
        waypointRoot.SetParent(pathObject.transform, false);
        for (int i = 0; i < points.Length; i++)
        {
            var waypoint = new GameObject($"W{i:00}").transform;
            waypoint.SetParent(waypointRoot, false);
            waypoint.position = points[i];
        }

        var path = pathObject.AddComponent<DroneFollowPath>();
        path.difficulty = index;
        return path;
    }

    /// <summary>Snaps the path's waypoints to the ground and regenerates one gate per waypoint except the start.</summary>
    public static int RebuildGates(DroneFollowPath path)
    {
        Transform waypointRoot = path.transform.Find(DroneFollowPath.WaypointRootName);
        if (waypointRoot == null || waypointRoot.childCount < 2)
        {
            Debug.LogError($"[DroneFollowTerrain] {path.name} needs a '{DroneFollowPath.WaypointRootName}' child with at least two waypoints.", path);
            return 0;
        }

        foreach (Transform waypoint in waypointRoot)
        {
            Undo.RecordObject(waypoint, "Snap waypoint to ground");
            if (TryGroundHeight(waypoint.position, out float groundY))
                waypoint.position = new Vector3(waypoint.position.x, groundY, waypoint.position.z);
            else
                Debug.LogError($"[DroneFollowTerrain] No {GroundTag} below waypoint {waypoint.name} of {path.name}.", waypoint);
        }

        Transform gateRoot = path.transform.Find(DroneFollowPath.GateRootName);
        if (gateRoot != null)
            Undo.DestroyObjectImmediate(gateRoot.gameObject);
        gateRoot = new GameObject(DroneFollowPath.GateRootName).transform;
        Undo.RegisterCreatedObjectUndo(gateRoot.gameObject, "Create gates");
        gateRoot.SetParent(path.transform, false);

        int count = waypointRoot.childCount;
        int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
        for (int i = 1; i < count; i++)
        {
            Vector3 previous = waypointRoot.GetChild(i - 1).position;
            Vector3 current = waypointRoot.GetChild(i).position;
            Vector3 next = waypointRoot.GetChild((i + 1) % count).position;
            Vector3 heading = Vector3.ProjectOnPlane((current - previous).normalized + (next - current).normalized, Vector3.up);
            if (heading.sqrMagnitude < 1e-4f)
                heading = Vector3.ProjectOnPlane(next - current, Vector3.up);

            var gateObject = new GameObject($"Gate{i:00}");
            gateObject.layer = ignoreRaycastLayer;
            gateObject.transform.SetParent(gateRoot, false);
            gateObject.transform.SetPositionAndRotation(
                current + Vector3.up * (GateHeight * 0.5f), Quaternion.LookRotation(heading.normalized));
            var box = gateObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(GateWidth, GateHeight, GateDepth);
            var gate = gateObject.AddComponent<DroneFollowGate>();
            gate.path = path;
            gate.waypointIndex = i;
        }
        EditorUtility.SetDirty(path);
        return count - 1;
    }

    static bool TryGroundHeight(Vector3 position, out float groundY)
    {
        groundY = 0f;
        float best = float.MaxValue;
        foreach (RaycastHit hit in Physics.RaycastAll(position + Vector3.up * 100f, Vector3.down, 300f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (!hit.collider.CompareTag(GroundTag) || hit.distance >= best)
                continue;
            best = hit.distance;
            groundY = hit.point.y;
        }
        return best < float.MaxValue;
    }

    static GameObject[] LoadVegetationPrefabs()
    {
        if (!AssetDatabase.IsValidFolder(VegetationPrefabFolder))
            return new GameObject[0];
        return AssetDatabase.FindAssets("t:Prefab", new[] { VegetationPrefabFolder })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(path => !ExcludedPrefabFragments.Any(path.Contains))
            .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
            .Where(prefab => prefab != null)
            .ToArray();
    }

    static int ScatterScenery(Transform parent, Terrain terrain, GameObject[] prefabs, List<Vector3[]> paths, Vector3 pad)
    {
        var root = new GameObject(SceneryRootName).transform;
        Undo.RegisterCreatedObjectUndo(root.gameObject, "Scatter scenery");
        root.SetParent(parent, false);
        var random = new System.Random(Seed);
        float Next(float min, float max) => min + (float)random.NextDouble() * (max - min);

        float half = TerrainSize * 0.5f - 5f;
        int placed = 0;
        for (int attempt = 0; attempt < SceneryCount * 20 && placed < SceneryCount; attempt++)
        {
            Vector3 position;
            // WHY(spec, perception): part of the scenery stands just outside the path corridor so it shows up
            // in the follow camera as distractors and depth structure; the rest fills the landscape.
            if (Next(0f, 1f) < NearPathShare)
            {
                Vector3[] loop = paths[random.Next(paths.Count)];
                int segment = random.Next(loop.Length);
                Vector3 a = loop[segment];
                Vector3 b = loop[(segment + 1) % loop.Length];
                Vector3 along = Vector3.Lerp(a, b, Next(0f, 1f));
                Vector3 side = Vector3.Cross(Vector3.up, (b - a).normalized) * (random.Next(2) == 0 ? -1f : 1f);
                position = along + side * Next(PathClearance, PathClearance + 10f);
            }
            else
                position = pad + new Vector3(Next(-half, half), 0f, Next(-half, half));

            if (Mathf.Abs(position.x - pad.x) > half || Mathf.Abs(position.z - pad.z) > half)
                continue;
            if (FlatDistance(position, pad) < PadClearance || DistanceToPaths(position, paths) < PathClearance)
                continue;

            GameObject prefab = prefabs[random.Next(prefabs.Length)];
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.gameObject.scene);
            instance.transform.SetParent(root, false);
            position.y = terrain.SampleHeight(position) + terrain.transform.position.y;
            instance.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, Next(0f, 360f), 0f));
            instance.transform.localScale = prefab.transform.localScale * Next(0.8f, 1.3f);
            placed++;
        }
        return placed;
    }

    static float FlatDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

    static float DistanceToPaths(Vector3 point, List<Vector3[]> paths)
    {
        float best = float.MaxValue;
        var flatPoint = new Vector2(point.x, point.z);
        foreach (Vector3[] loop in paths)
            for (int i = 0; i < loop.Length; i++)
            {
                var a = new Vector2(loop[i].x, loop[i].z);
                var b = new Vector2(loop[(i + 1) % loop.Length].x, loop[(i + 1) % loop.Length].z);
                Vector2 ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(flatPoint - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
                best = Mathf.Min(best, Vector2.Distance(flatPoint, a + ab * t));
            }
        return best;
    }

    static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder))
            return;
        string parentFolder = Path.GetDirectoryName(folder)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(parentFolder))
            EnsureFolder(parentFolder);
        AssetDatabase.CreateFolder(parentFolder, Path.GetFileName(folder));
    }
}
