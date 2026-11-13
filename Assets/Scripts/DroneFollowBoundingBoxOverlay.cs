using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Renders the delayed detector box over the camera's RawImage preview. The box
/// is visualization only; policy observations remain the four-value contract.
/// </summary>
[RequireComponent(typeof(RectTransform), typeof(CanvasRenderer))]
public sealed class DroneFollowBoundingBoxOverlay : MaskableGraphic
{
    public SimulatedBoundingBoxDetector detector;
    [Min(1f)] public float lineThickness = 2f;

    Rect _viewportBox;
    bool _visible;

    protected override void Awake()
    {
        base.Awake();
        raycastTarget = false;
    }

    void LateUpdate()
    {
        bool visible = detector != null && detector.TryGetCurrentViewportBox(out Rect nextBox);
        if (visible == _visible && (!visible || Approximately(_viewportBox, nextBox)))
            return;

        _visible = visible;
        _viewportBox = visible ? nextBox : Rect.zero;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vertexHelper)
    {
        vertexHelper.Clear();
        if (!_visible || _viewportBox.width <= 0f || _viewportBox.height <= 0f)
            return;

        Rect area = rectTransform.rect;
        float xMin = Mathf.Lerp(area.xMin, area.xMax, _viewportBox.xMin);
        float xMax = Mathf.Lerp(area.xMin, area.xMax, _viewportBox.xMax);
        float yMin = Mathf.Lerp(area.yMin, area.yMax, _viewportBox.yMin);
        float yMax = Mathf.Lerp(area.yMin, area.yMax, _viewportBox.yMax);
        float thickness = Mathf.Clamp(lineThickness, 1f, Mathf.Max(1f, Mathf.Min(xMax - xMin, yMax - yMin) * 0.25f));

        AddQuad(vertexHelper, xMin, yMax - thickness, xMax, yMax);
        AddQuad(vertexHelper, xMin, yMin, xMax, yMin + thickness);
        AddQuad(vertexHelper, xMin, yMin + thickness, xMin + thickness, yMax - thickness);
        AddQuad(vertexHelper, xMax - thickness, yMin + thickness, xMax, yMax - thickness);
    }

    void AddQuad(VertexHelper vertexHelper, float xMin, float yMin, float xMax, float yMax)
    {
        int first = vertexHelper.currentVertCount;
        UIVertex vertex = UIVertex.simpleVert;
        vertex.color = color;

        vertex.position = new Vector3(xMin, yMin, 0f);
        vertexHelper.AddVert(vertex);
        vertex.position = new Vector3(xMin, yMax, 0f);
        vertexHelper.AddVert(vertex);
        vertex.position = new Vector3(xMax, yMax, 0f);
        vertexHelper.AddVert(vertex);
        vertex.position = new Vector3(xMax, yMin, 0f);
        vertexHelper.AddVert(vertex);

        vertexHelper.AddTriangle(first, first + 1, first + 2);
        vertexHelper.AddTriangle(first, first + 2, first + 3);
    }

    static bool Approximately(Rect a, Rect b)
    {
        const float epsilon = 0.00001f;
        return Mathf.Abs(a.x - b.x) < epsilon &&
               Mathf.Abs(a.y - b.y) < epsilon &&
               Mathf.Abs(a.width - b.width) < epsilon &&
               Mathf.Abs(a.height - b.height) < epsilon;
    }
}
