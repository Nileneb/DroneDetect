using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// FusionMapRenderer — Visualisiert die Fusion-Map und Anomalien in Unity.
///
/// Empfaengt Map-Daten von DroneBridgeService (Event-basiert) und
/// rendert sie als farbige Punkt-Wolke oder Marker.
///
/// Rendering-Regeln (aus Guide):
///   - is_anomaly = true  -> rote Marker (groesser)
///   - source enthaelt osm -> 2D Landmark-Punkte (Gebaeude/Strassen, z=0)
///   - source enthaelt wifi -> dynamische Anomalie-Overlays
///   - label -> optionaler Text (Billboard)
///
/// Nutzung von /map/anomalies fuer:
///   - Alert-UI
///   - Reward-Shaping in ML-Agents
///
/// Optimierung:
///   - Punkt-Pool (Object-Pool) fuer GC-freies Rendering
///   - Batch-Updates: nur bei neuen Map-Daten, nicht pro Frame
///   - Cached Mesh fuer grosse Punkt-Wolken (>1000 Punkte)
///
/// Montage: Auf ein leeres GameObject in der Szene legen.
/// DroneBridgeService-Referenz im Inspector zuweisen.
/// </summary>
public class FusionMapRenderer : MonoBehaviour
{
    // ──────────────── Inspector ────────────────

    [Header("Data Source")]
    [Tooltip("Referenz auf den Bridge-Service (liefert Map + Anomalie-Events)")]
    public DroneBridgeService bridgeService;

    [Header("Rendering")]
    [Tooltip("Parent-Transform fuer alle erzeugten Punkte/Marker")]
    public Transform mapRoot;

    [Tooltip("Prefab fuer normale Map-Punkte (Sphere, Cube, etc.)")]
    public GameObject pointPrefab;

    [Tooltip("Prefab fuer Anomalie-Marker (auffaelliger, groesser)")]
    public GameObject anomalyPrefab;

    [Header("Colors")]
    public Color osmColor = new Color(0.8f, 0.8f, 0.8f, 0.6f);
    public Color wifiColor = new Color(0.2f, 0.8f, 0.2f, 0.8f);
    public Color anomalyColor = new Color(1.0f, 0.1f, 0.1f, 1.0f);
    public Color defaultColor = new Color(0.6f, 0.6f, 0.6f, 0.5f);

    [Header("Sizes")]
    [Tooltip("Skalierung normaler Punkte")]
    public float pointScale = 0.15f;

    [Tooltip("Skalierung von Anomalie-Markern")]
    public float anomalyScale = 0.4f;

    [Header("Performance")]
    [Tooltip("Maximale Anzahl sichtbarer Punkte (Pool-Groesse)")]
    public int maxVisiblePoints = 5000;

    [Tooltip("Nutze Mesh-Instancing fuer grosse Punkt-Wolken")]
    public bool useMeshInstancing = true;

    [Header("Debug")]
    public bool logUpdates = false;

    // ──────────────── State ────────────────

    /// <summary>Aktuelle Anzahl gerenderter Punkte.</summary>
    public int RenderedPointCount { get; private set; }

    /// <summary>Aktuelle Anzahl gerenderter Anomalien.</summary>
    public int RenderedAnomalyCount { get; private set; }

    // ──────────────── Internals ────────────────

    // Object pool
    List<GameObject> _pointPool = new List<GameObject>();
    List<GameObject> _anomalyPool = new List<GameObject>();
    int _activePoints;
    int _activeAnomalies;

    // Mesh instancing
    Mesh _pointMesh;
    Material _instanceMaterial;
    Matrix4x4[] _matrices;
    Vector4[] _colors;
    MaterialPropertyBlock _mpb;
    bool _meshDirty;

    // ══════════════════════════════════════════════════════════
    // Lifecycle
    // ══════════════════════════════════════════════════════════

    void Awake()
    {
        if (mapRoot == null)
        {
            var go = new GameObject("FusionMapRoot");
            go.transform.SetParent(transform);
            go.transform.localPosition = Vector3.zero;
            mapRoot = go.transform;
        }

        CreateDefaultPrefabs();
        InitPool();

        if (useMeshInstancing)
            InitMeshInstancing();
    }

    void OnEnable()
    {
        if (bridgeService != null)
        {
            bridgeService.OnMapUpdated += HandleMapUpdated;
            bridgeService.OnAnomaliesUpdated += HandleAnomaliesUpdated;
        }
    }

    void OnDisable()
    {
        if (bridgeService != null)
        {
            bridgeService.OnMapUpdated -= HandleMapUpdated;
            bridgeService.OnAnomaliesUpdated -= HandleAnomaliesUpdated;
        }
    }

    void Update()
    {
        if (useMeshInstancing && _meshDirty && _activePoints > 0)
        {
            RenderMeshInstanced();
            _meshDirty = false;
        }
    }

    // ══════════════════════════════════════════════════════════
    // Default Prefabs (Fallbacks wenn nichts zugewiesen)
    // ══════════════════════════════════════════════════════════

    void CreateDefaultPrefabs()
    {
        if (pointPrefab == null)
        {
            pointPrefab = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            pointPrefab.transform.localScale = Vector3.one * pointScale;
            // Collider entfernen (Performance)
            var col = pointPrefab.GetComponent<Collider>();
            if (col != null) Destroy(col);
            pointPrefab.SetActive(false);
            pointPrefab.name = "_PointPrefab";
        }

        if (anomalyPrefab == null)
        {
            anomalyPrefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            anomalyPrefab.transform.localScale = Vector3.one * anomalyScale;
            var col = anomalyPrefab.GetComponent<Collider>();
            if (col != null) Destroy(col);
            anomalyPrefab.SetActive(false);
            anomalyPrefab.name = "_AnomalyPrefab";
        }
    }

    // ══════════════════════════════════════════════════════════
    // Object Pool
    // ══════════════════════════════════════════════════════════

    void InitPool()
    {
        // Pre-allocate some pool objects
        int preAllocNormal = Mathf.Min(200, maxVisiblePoints);
        for (int i = 0; i < preAllocNormal; i++)
        {
            var go = CreatePoolObject(pointPrefab, pointScale);
            _pointPool.Add(go);
        }

        int preAllocAnomaly = Mathf.Min(50, maxVisiblePoints / 10);
        for (int i = 0; i < preAllocAnomaly; i++)
        {
            var go = CreatePoolObject(anomalyPrefab, anomalyScale);
            _anomalyPool.Add(go);
        }
    }

    GameObject CreatePoolObject(GameObject prefab, float scale)
    {
        var go = Instantiate(prefab, mapRoot);
        go.transform.localScale = Vector3.one * scale;
        go.SetActive(false);
        return go;
    }

    GameObject GetFromPool(List<GameObject> pool, GameObject prefab, float scale, ref int activeCount)
    {
        // Try to reuse an inactive object
        if (activeCount < pool.Count)
        {
            var go = pool[activeCount];
            go.SetActive(true);
            activeCount++;
            return go;
        }

        // Expand pool
        if (pool.Count < maxVisiblePoints)
        {
            var go = CreatePoolObject(prefab, scale);
            go.SetActive(true);
            pool.Add(go);
            activeCount++;
            return go;
        }

        return null; // Pool exhausted
    }

    void DeactivatePool(List<GameObject> pool, ref int activeCount)
    {
        for (int i = 0; i < activeCount && i < pool.Count; i++)
            pool[i].SetActive(false);
        activeCount = 0;
    }

    // ══════════════════════════════════════════════════════════
    // Mesh Instancing (for large point clouds)
    // ══════════════════════════════════════════════════════════

    void InitMeshInstancing()
    {
        _pointMesh = CreateQuadMesh();
        _instanceMaterial = new Material(Shader.Find("Particles/Standard Unlit"));
        _instanceMaterial.enableInstancing = true;
        _matrices = new Matrix4x4[maxVisiblePoints];
        _colors = new Vector4[maxVisiblePoints];
        _mpb = new MaterialPropertyBlock();
    }

    static Mesh CreateQuadMesh()
    {
        var mesh = new Mesh { name = "FusionPoint" };
        float s = 0.5f;
        mesh.vertices = new Vector3[]
        {
            new Vector3(-s, -s, 0), new Vector3(s, -s, 0),
            new Vector3(s, s, 0), new Vector3(-s, s, 0)
        };
        mesh.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
        mesh.normals = new Vector3[] { -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward };
        mesh.uv = new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        mesh.RecalculateBounds();
        return mesh;
    }

    void RenderMeshInstanced()
    {
        if (_pointMesh == null || _instanceMaterial == null) return;

        int count = Mathf.Min(_activePoints, maxVisiblePoints);
        if (count <= 0) return;

        // DrawMeshInstanced supports max 1023 per call
        int batch = 0;
        while (batch < count)
        {
            int batchSize = Mathf.Min(1023, count - batch);

            var slice = new Matrix4x4[batchSize];
            Array.Copy(_matrices, batch, slice, 0, batchSize);

            var colSlice = new Vector4[batchSize];
            Array.Copy(_colors, batch, colSlice, 0, batchSize);

            _mpb.SetVectorArray("_Color", colSlice);
            Graphics.DrawMeshInstanced(_pointMesh, 0, _instanceMaterial, slice, batchSize, _mpb);

            batch += batchSize;
        }
    }

    // ══════════════════════════════════════════════════════════
    // Event Handlers
    // ══════════════════════════════════════════════════════════

    void HandleMapUpdated(SensorAdapter.FusionMapResponse map)
    {
        if (map == null || map.points == null) return;

        if (logUpdates)
            Debug.Log($"[FusionMap] Received {map.n_voxels} voxels, {map.points.Count} points");

        if (useMeshInstancing)
            UpdateMeshInstanced(map);
        else
            UpdateGameObjects(map);
    }

    void HandleAnomaliesUpdated(SensorAdapter.AnomalyListResponse anomalies)
    {
        if (anomalies == null || anomalies.anomalies == null) return;

        if (logUpdates)
            Debug.Log($"[FusionMap] Received {anomalies.anomalies.Count} anomalies");

        UpdateAnomalyMarkers(anomalies);
    }

    // ══════════════════════════════════════════════════════════
    // GameObject-based Rendering (< 1000 points)
    // ══════════════════════════════════════════════════════════

    void UpdateGameObjects(SensorAdapter.FusionMapResponse map)
    {
        // Deactivate old
        DeactivatePool(_pointPool, ref _activePoints);

        int count = Mathf.Min(map.points.Count, maxVisiblePoints);

        for (int i = 0; i < count; i++)
        {
            var pt = map.points[i];
            if (pt.position == null || pt.position.Length < 3) continue;

            // Skip anomalies (rendered separately)
            if (pt.is_anomaly) continue;

            var go = GetFromPool(_pointPool, pointPrefab, pointScale, ref _activePoints);
            if (go == null) break;

            // ENU -> Unity coordinates
            go.transform.localPosition = SensorAdapter.EnuToUnity(pt.position);

            // Color by source
            var color = ClassifyColor(pt);
            SetColor(go, color);
        }

        RenderedPointCount = _activePoints;
    }

    // ══════════════════════════════════════════════════════════
    // Mesh Instancing Rendering (> 1000 points)
    // ══════════════════════════════════════════════════════════

    void UpdateMeshInstanced(SensorAdapter.FusionMapResponse map)
    {
        _activePoints = 0;
        int count = Mathf.Min(map.points.Count, maxVisiblePoints);

        for (int i = 0; i < count; i++)
        {
            var pt = map.points[i];
            if (pt.position == null || pt.position.Length < 3) continue;

            Vector3 uPos = SensorAdapter.EnuToUnity(pt.position);
            float scale = pt.is_anomaly ? anomalyScale : pointScale;
            Color color = pt.is_anomaly ? anomalyColor : ClassifyColor(pt);

            _matrices[_activePoints] = Matrix4x4.TRS(uPos, Quaternion.identity, Vector3.one * scale);
            _colors[_activePoints] = color;
            _activePoints++;
        }

        RenderedPointCount = _activePoints;
        _meshDirty = true;
    }

    // ══════════════════════════════════════════════════════════
    // Anomaly Markers (always GameObject-based for interaction)
    // ══════════════════════════════════════════════════════════

    void UpdateAnomalyMarkers(SensorAdapter.AnomalyListResponse anomalies)
    {
        DeactivatePool(_anomalyPool, ref _activeAnomalies);

        for (int i = 0; i < anomalies.anomalies.Count; i++)
        {
            var a = anomalies.anomalies[i];
            if (a.position == null || a.position.Length < 3) continue;

            var go = GetFromPool(_anomalyPool, anomalyPrefab, anomalyScale, ref _activeAnomalies);
            if (go == null) break;

            go.transform.localPosition = SensorAdapter.EnuToUnity(a.position);
            SetColor(go, anomalyColor);
            go.name = $"Anomaly_{a.label}_{a.score:F2}";
        }

        RenderedAnomalyCount = _activeAnomalies;
    }

    // ══════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Bestimmt die Farbe eines Punkts anhand seiner Sources.
    /// - source enthaelt osm -> 2D Landmark-Punkte (Gebaeude/Strassen)
    /// - source enthaelt wifi -> dynamische Anomalie-Overlays
    /// </summary>
    Color ClassifyColor(SensorAdapter.FusionPoint pt)
    {
        if (pt.sources != null)
        {
            foreach (var src in pt.sources)
            {
                if (src == null) continue;
                string s = src.ToLowerInvariant();
                if (s.Contains("wifi")) return wifiColor;
                if (s.Contains("osm")) return osmColor;
            }
        }

        // Fallback: Label-basiert
        if (!string.IsNullOrEmpty(pt.label))
        {
            string l = pt.label.ToLowerInvariant();
            if (l.Contains("cable") || l.Contains("anomal")) return anomalyColor;
            if (l.Contains("building") || l.Contains("road")) return osmColor;
        }

        return defaultColor;
    }

    /// <summary>Setzt die Farbe eines GameObjects (MeshRenderer).</summary>
    static void SetColor(GameObject go, Color color)
    {
        var renderer = go.GetComponent<Renderer>();
        if (renderer != null)
        {
            // MaterialPropertyBlock fuer GC-freies Setzen
            var mpb = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(mpb);
            mpb.SetColor("_Color", color);
            renderer.SetPropertyBlock(mpb);
        }
    }

    /// <summary>Entfernt alle sichtbaren Punkte und Anomalien (z.B. bei Episode-Reset).</summary>
    public void ClearAll()
    {
        DeactivatePool(_pointPool, ref _activePoints);
        DeactivatePool(_anomalyPool, ref _activeAnomalies);
        _meshDirty = false;
        RenderedPointCount = 0;
        RenderedAnomalyCount = 0;
    }

    // ──────────────── Gizmos ────────────────

    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;

        // Anomalien als rote Kreuze anzeigen
        if (_anomalyPool != null)
        {
            Gizmos.color = anomalyColor;
            for (int i = 0; i < _activeAnomalies && i < _anomalyPool.Count; i++)
            {
                if (_anomalyPool[i].activeSelf)
                {
                    var p = _anomalyPool[i].transform.position;
                    float s = anomalyScale;
                    Gizmos.DrawLine(p - Vector3.right * s, p + Vector3.right * s);
                    Gizmos.DrawLine(p - Vector3.up * s, p + Vector3.up * s);
                    Gizmos.DrawLine(p - Vector3.forward * s, p + Vector3.forward * s);
                }
            }
        }
    }
}
