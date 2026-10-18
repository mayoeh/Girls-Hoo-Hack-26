using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class NoseShape
{
    public const float Gap = 0.03f;
    public const float MinLength = 0.3f, MaxLength = 5f;
    public const int MinPoints = 1, MaxPoints = 12;

    [Tooltip("Tip to shoulder, as a multiple of the body radius. 1 = hemisphere.")]
    [Range(0.3f, 3f)] public float length = 1f;

    [Tooltip("Tip height above (+) or below (-) the body's centre line, as a fraction of the body radius.")]
    [Range(-1f, 1f)] public float tipHeight = 0f;

    [Tooltip("On: the bottom mirrors the top. Off: top and bottom are shaped separately.")]
    public bool mirrored = true;

    [Tooltip("Top shape points. x: 0 = tip, 1 = shoulder. y: radius as a fraction of the body radius.")]
    public List<Vector2> points = new();

    [Tooltip("Bottom shape points, used when Mirrored is off. Always the same count as Points.")]
    public List<Vector2> bottom = new();

    // The list that controls a side. While mirrored, both sides use the top list.
    public List<Vector2> Side(bool isBottom) => isBottom && !mirrored ? bottom : points;

    public static NoseShape CleanBase(int count = 3)
    {
        var s = new NoseShape { length = 1f, mirrored = true };
        for (int k = 1; k <= count; k++)
        {
            float u = k / (count + 1f);
            float d = 1f - u;                                   // distance from the shoulder, in body radii
            s.points.Add(new Vector2(u, Mathf.Sqrt(1f - d * d)));
        }
        s.bottom = new List<Vector2>(s.points);
        return s;
    }

    // A clean sphere-cone: a round tip (radius = bluntness × body radius) blending into a straight cone.
    // limited = true if the cone would need a nose longer than MaxLength, so it was squeezed to fit.
    public static NoseShape SphereCone(float fullAngleDeg, float bluntness, int count, out bool limited)
    {
        float th = Mathf.Clamp(fullAngleDeg * 0.5f, 0.5f, 85f) * Mathf.Deg2Rad;
        float rho = Mathf.Clamp01(bluntness);
        float sin = Mathf.Sin(th), cos = Mathf.Cos(th), tan = Mathf.Tan(th);

        float fullLength = (cos - rho * (1f - sin)) / sin;    // tip to where the cone reaches the body radius
        float length = Mathf.Clamp(fullLength, MinLength, MaxLength);
        limited = fullLength > MaxLength;
        float tangent = rho * (1f - sin);                       // where the round tip meets the straight cone

        var s = new NoseShape { length = length, mirrored = true };
        for (int k = 1; k <= count; k++)
        {
            float u = k / (count + 1f);
            float d = u * fullLength;                           // distance from the tip
            float r = d < tangent
                ? Mathf.Sqrt(Mathf.Max(0f, rho * rho - (rho - d) * (rho - d)))
                : rho * cos + (d - tangent) * tan;
            s.points.Add(new Vector2(u, Mathf.Clamp01(r)));
        }
        s.bottom = new List<Vector2>(s.points);
        return s;
    }

    public NoseShape Clone() => new NoseShape
    {
        length = length,
        tipHeight = tipHeight,
        mirrored = mirrored,
        points = new List<Vector2>(points),
        bottom = new List<Vector2>(bottom),
    };

    public void Sanitize()
    {
        length = Mathf.Clamp(length, MinLength, MaxLength);
        tipHeight = Mathf.Clamp(tipHeight, -1f, 1f);
        if (points.Count == 0) points.Add(new Vector2(0.5f, 0.85f));
        if (points.Count > MaxPoints) points.RemoveRange(MaxPoints, points.Count - MaxPoints);

        // While mirrored, the bottom is kept as a copy of the top, so turning mirroring off starts symmetric.
        if (mirrored || bottom.Count != points.Count) bottom = new List<Vector2>(points);

        SanitizeList(points);
        SanitizeList(bottom);
    }

    static void SanitizeList(List<Vector2> pts)
    {
        pts.Sort((a, b) => a.x.CompareTo(b.x));
        for (int i = 0; i < pts.Count; i++)
        {
            float min = Gap * (i + 1), max = 1f - Gap * (pts.Count - i);
            float x = Mathf.Clamp(pts[i].x, min, max);
            if (i > 0) x = Mathf.Max(x, pts[i - 1].x + Gap);
            pts[i] = new Vector2(x, Mathf.Clamp01(pts[i].y));
        }
    }

    // True if the two shapes are the same (so a click that didn't move anything doesn't create an undo step).
    public bool SameAs(NoseShape o)
    {
        if (o == null || mirrored != o.mirrored) return false;
        if (points.Count != o.points.Count || bottom.Count != o.bottom.Count) return false;
        if (Mathf.Abs(length - o.length) > 1e-5f || Mathf.Abs(tipHeight - o.tipHeight) > 1e-5f) return false;
        for (int i = 0; i < points.Count; i++)
            if ((points[i] - o.points[i]).sqrMagnitude > 1e-10f) return false;
        for (int i = 0; i < bottom.Count; i++)
            if ((bottom[i] - o.bottom[i]).sqrMagnitude > 1e-10f) return false;
        return true;
    }
}