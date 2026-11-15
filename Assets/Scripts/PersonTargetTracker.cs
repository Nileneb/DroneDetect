using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Keeps the follow target's identity among several detected people.
/// The target is locked once (operator tap on the phone, or automatically on the
/// most confident person), then each frame picks the detection that best matches
/// its recent position and its appearance gallery: colour signatures collected
/// from the different views seen while tracking was certain.
/// Boxes are normalized image coordinates (0..1, origin top-left), as YOLO reports them.
/// </summary>
public sealed class PersonTargetTracker
{
    public struct Candidate
    {
        public Rect Box;
        public float Score;
        public float[] Signature;
    }

    /// <summary>How signatures are compared and which similarity counts as "same person".</summary>
    public sealed class AppearanceModel
    {
        public System.Func<float[], float[], float> Similarity;
        public float ReacquireThreshold;
        public float NewViewThreshold;

        /// <summary>Colour histograms (Bhattacharyya coefficient).</summary>
        public static readonly AppearanceModel ColourHistogram = new AppearanceModel
        {
            Similarity = HistogramSimilarity,
            ReacquireThreshold = 0.75f,
            NewViewThreshold = 0.9f
        };

        /// <summary>L2-normalized OSNet embeddings (cosine similarity).</summary>
        public static readonly AppearanceModel ReidEmbedding = new AppearanceModel
        {
            Similarity = CosineSimilarity,
            ReacquireThreshold = 0.7f,
            NewViewThreshold = 0.85f
        };
    }

    const int MaxGallerySize = 40;
    const float RecentTrackSeconds = 0.6f;
    const float TrackAcceptScore = 0.5f;
    const float ConsistentIoU = 0.3f;

    readonly AppearanceModel _appearance;

    readonly List<float[]> _gallery = new List<float[]>();
    Rect _lastBox;
    float _lastSeenTime = float.NegativeInfinity;
    bool _lockRequested;
    Vector2 _lockPoint;

    public PersonTargetTracker(AppearanceModel appearance = null)
    {
        _appearance = appearance ?? AppearanceModel.ColourHistogram;
    }

    public bool HasLock { get; private set; }
    public int GallerySize => _gallery.Count;
    public float LastAppearanceMatch { get; private set; }

    public void Reset()
    {
        HasLock = false;
        _lockRequested = false;
        _gallery.Clear();
        _lastSeenTime = float.NegativeInfinity;
    }

    /// <summary>Lock onto the person at <paramref name="normalizedPoint"/> (0..1, top-left) in the next frame.</summary>
    public void RequestLock(Vector2 normalizedPoint)
    {
        _lockRequested = true;
        _lockPoint = normalizedPoint;
    }

    /// <summary>Returns the index of the target among <paramref name="candidates"/>, or -1 if it is not visible.</summary>
    public int Select(IReadOnlyList<Candidate> candidates, float time, bool autoLock)
    {
        if (candidates.Count == 0)
            return -1;

        if (_lockRequested)
        {
            _lockRequested = false;
            return Lock(candidates, NearestTo(candidates, _lockPoint), time);
        }

        if (!HasLock)
            return autoLock ? Lock(candidates, MostConfident(candidates), time) : -1;

        bool recentlySeen = time - _lastSeenTime < RecentTrackSeconds;
        int best = -1;
        float bestScore = float.NegativeInfinity;
        float bestAppearance = 0f;
        float bestIoU = 0f;
        for (int i = 0; i < candidates.Count; i++)
        {
            float appearance = AppearanceMatch(candidates[i].Signature);
            float iou = IoU(candidates[i].Box, _lastBox);
            float proximity = Mathf.Clamp01(1f - Vector2.Distance(candidates[i].Box.center, _lastBox.center) / 0.3f);
            float score = recentlySeen
                ? 0.5f * appearance + 0.5f * Mathf.Max(iou, proximity)
                : appearance;
            if (score > bestScore)
            {
                best = i;
                bestScore = score;
                bestAppearance = appearance;
                bestIoU = iou;
            }
        }

        bool accepted = recentlySeen
            ? bestScore >= TrackAcceptScore || (candidates.Count == 1 && bestIoU >= ConsistentIoU)
            : bestAppearance >= _appearance.ReacquireThreshold;
        LastAppearanceMatch = bestAppearance;
        if (!accepted)
            return -1;

        // Only geometrically consistent matches teach the gallery a new view, so a wrong match cannot drift it.
        if (recentlySeen && bestIoU >= ConsistentIoU && bestAppearance < _appearance.NewViewThreshold)
            AddToGallery(candidates[best].Signature);
        _lastBox = candidates[best].Box;
        _lastSeenTime = time;
        return best;
    }

    int Lock(IReadOnlyList<Candidate> candidates, int index, float time)
    {
        _gallery.Clear();
        AddToGallery(candidates[index].Signature);
        HasLock = true;
        _lastBox = candidates[index].Box;
        _lastSeenTime = time;
        LastAppearanceMatch = 1f;
        return index;
    }

    void AddToGallery(float[] signature)
    {
        if (signature == null)
            return;
        // The first view (the operator's lock) is kept for good; later views rotate.
        if (_gallery.Count >= MaxGallerySize)
            _gallery.RemoveAt(1);
        _gallery.Add(signature);
    }

    float AppearanceMatch(float[] signature)
    {
        if (signature == null || _gallery.Count == 0)
            return 0f;
        float best = 0f;
        foreach (float[] view in _gallery)
            best = Mathf.Max(best, _appearance.Similarity(signature, view));
        return best;
    }

    public static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0f;
        for (int i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }

    /// <summary>Bhattacharyya coefficient of two L1-normalized histograms: 1 = identical.</summary>
    public static float HistogramSimilarity(float[] a, float[] b)
    {
        int parts = a.Length / AppearanceSignature.BinsPerPart;
        float sum = 0f;
        for (int i = 0; i < a.Length; i++)
            sum += Mathf.Sqrt(a[i] * b[i]);
        return sum / parts;
    }

    static int NearestTo(IReadOnlyList<Candidate> candidates, Vector2 point)
    {
        int best = 0;
        float bestDistance = float.PositiveInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            float distance = candidates[i].Box.Contains(point) ? 0f : Vector2.Distance(candidates[i].Box.center, point);
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    static int MostConfident(IReadOnlyList<Candidate> candidates)
    {
        int best = 0;
        for (int i = 1; i < candidates.Count; i++)
            if (candidates[i].Score > candidates[best].Score)
                best = i;
        return best;
    }

    public static float IoU(Rect a, Rect b)
    {
        float width = Mathf.Max(0f, Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin));
        float height = Mathf.Max(0f, Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin));
        float intersection = width * height;
        float union = a.width * a.height + b.width * b.height - intersection;
        return union > 0f ? intersection / union : 0f;
    }
}

/// <summary>
/// Colour signature of a person box: hue/saturation histograms of the upper and lower body,
/// which separate people by clothing and are cheap enough to compute for every detection.
/// </summary>
public static class AppearanceSignature
{
    const int HueBins = 12;
    const int SaturationBins = 4;
    public const int BinsPerPart = HueBins * SaturationBins + 1;  // + 1 bin for near-grey pixels
    const int Parts = 2;

    /// <summary>
    /// <paramref name="pixels"/> is a bottom-up RGBA image of <paramref name="width"/> x <paramref name="height"/>;
    /// <paramref name="box"/> is normalized with a top-left origin.
    /// </summary>
    public static float[] Compute(Color32[] pixels, int width, int height, Rect box)
    {
        var histogram = new float[BinsPerPart * Parts];
        // Shrink the box so background at the edges does not dominate the signature.
        Rect inner = new Rect(box.x + box.width * 0.2f, box.y + box.height * 0.1f, box.width * 0.6f, box.height * 0.8f);
        int x0 = Mathf.Clamp(Mathf.FloorToInt(inner.xMin * width), 0, width - 1);
        int x1 = Mathf.Clamp(Mathf.CeilToInt(inner.xMax * width), x0 + 1, width);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(inner.yMin * height), 0, height - 1);
        int y1 = Mathf.Clamp(Mathf.CeilToInt(inner.yMax * height), y0 + 1, height);
        int split = (y0 + y1) / 2;
        var counts = new float[Parts];

        for (int y = y0; y < y1; y++)
        {
            int part = y < split ? 0 : 1;
            int row = (height - 1 - y) * width;
            for (int x = x0; x < x1; x++)
            {
                Color32 c = pixels[row + x];
                Color.RGBToHSV(c, out float h, out float s, out float v);
                int bin = s < 0.15f || v < 0.15f
                    ? BinsPerPart - 1
                    : Mathf.Min(HueBins - 1, (int)(h * HueBins)) * SaturationBins +
                      Mathf.Min(SaturationBins - 1, (int)(s * SaturationBins));
                histogram[part * BinsPerPart + bin]++;
                counts[part]++;
            }
        }

        for (int part = 0; part < Parts; part++)
        {
            if (counts[part] <= 0f)
                continue;
            for (int bin = 0; bin < BinsPerPart; bin++)
                histogram[part * BinsPerPart + bin] /= counts[part];
        }
        return histogram;
    }
}
