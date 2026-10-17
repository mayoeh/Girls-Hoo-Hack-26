using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class RocketModel : MonoBehaviour
{
    [Header("Body (fixed)")]
    [Range(0.1f, 0.5f)] public float bodyRadius = 0.25f;
    [Range(0.5f, 3f)]   public float bodyLength = 1.5f;

    [Header("Nose")]
    public NoseShape shape = NoseShape.CleanBase();

    [Header("Mesh detail")]
    [Range(8, 96)] public int noseRings = 48;
    [Range(8, 64)] public int segments = 48;

    [Header("Flow paint (test mode)")]
    public Color metalColor = new(0.62f, 0.62f, 0.65f);
    public Color laminarPaint = new(0.25f, 0.6f, 1.2f);
    public Color turbulentPaint = new(1.25f, 0.35f, 0.12f);

    readonly List<Color> colors = new();

    public float NoseLength => shape.length * bodyRadius;
    public int ControlPointCount => shape.points.Count + 2;    // tip + shape points + shoulder

    // One curve per side. Knots: x along the axis, s = radius squared.
    class SideCurve
    {
        public readonly List<float> x = new(), s = new();
        public float[] m = new float[0];
    }
    readonly SideCurve topCurve = new(), bottomCurve = new();

    // Rings along the model, each with a top and a bottom radius.
    readonly List<float> ringX = new(), ringTop = new(), ringBottom = new(), ringCenter = new();
    Mesh mesh;
    readonly List<Vector3> verts = new();
    readonly List<Vector3> normals = new();
    readonly List<Vector2> uvs = new();
    readonly List<int> tris = new();

    void OnEnable()
    {
        if (mesh == null)
            mesh = new Mesh { name = "Rocket Model", hideFlags = HideFlags.DontSave };
        GetComponent<MeshFilter>().sharedMesh = mesh;
        Rebuild();
    }

    void OnValidate()
    {
        if (mesh != null) Rebuild();
    }

    void OnDestroy()
    {
        if (mesh == null) return;
        if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
    }

    [ContextMenu("Reset Nose to Clean Base")]
    public void ResetToCleanBase()
    {
        shape = NoseShape.CleanBase(shape.points.Count);
        Rebuild();
    }

    // Change the number of dots per side without changing the shape: new dots are placed on the current curve.
    public void SetPointCount(int count)
    {
        count = Mathf.Clamp(count, NoseShape.MinPoints, NoseShape.MaxPoints);
        if (count == shape.points.Count) return;
        var newTop = Resample(false, count);
        var newBottom = Resample(true, count);
        shape.points = newTop;
        shape.bottom = newBottom;
        Rebuild();
    }

    List<Vector2> Resample(bool isBottom, int count)
    {
        var list = new List<Vector2>(count);
        float L = NoseLength;
        for (int k = 1; k <= count; k++)
        {
            float u = k / (count + 1f);
            list.Add(new Vector2(u, RadiusAt(-L * (1f - u), isBottom) / bodyRadius));
        }
        return list;
    }

    // Control point i as (x, radius) for one side. 0 = tip, last = shoulder.
    public Vector2 ControlPoint(int i, bool isBottom = false)
    {
        float L = NoseLength;
        if (i == 0) return new Vector2(-L, 0f);
        if (i == ControlPointCount - 1) return new Vector2(0f, bodyRadius);
        Vector2 p = shape.Side(isBottom)[i - 1];
        return new Vector2(-L * (1f - p.x), p.y * bodyRadius);
    }

    public float RadiusAt(float x, bool isBottom = false)
    {
        if (x >= 0f) return x <= bodyLength ? bodyRadius : 0f;
        if (x <= -NoseLength) return 0f;
        var c = isBottom && !shape.mirrored ? bottomCurve : topCurve;
        return Mathf.Sqrt(Mathf.Max(0f, NoseCurve.Evaluate(c.x, c.s, c.m, x)));
    }

    // Slope of radius^2 at the tip. It equals 2 × the tip's radius of curvature, which is what bluntness measures.
    public float TipSlope(bool isBottom = false)
    {
        var c = isBottom && !shape.mirrored ? bottomCurve : topCurve;
        return c.m.Length > 0 ? c.m[0] : 0f;
    }

    // Height of the nose's centre line at x. The full tip height at the tip, fading to 0 (and flat) at the shoulder.
    public float CenterAt(float x)
    {
        if (x >= 0f) return 0f;
        float k = Mathf.Clamp01(-x / NoseLength);    // 1 at the tip, 0 at the shoulder
        return shape.tipHeight * bodyRadius * k * k;
    }

    // Where a dot sits in model space: the centre line, plus or minus that side's radius.
    public Vector2 DotPosition(int i, bool isBottom)
    {
        Vector2 c = ControlPoint(i, isBottom);
        return new Vector2(c.x, CenterAt(c.x) + (isBottom ? -c.y : c.y));
    }

    // Measure any shape without changing the model. The dashboard uses this to fit clean cones.
    public ShapeMetrics MeasureShape(NoseShape s)
    {
        s.Sanitize();
        var saved = shape;
        shape = s;
        BuildCurve(topCurve, false);
        BuildCurve(bottomCurve, true);
        var result = ShapeMetrics.Measure(this);
        shape = saved;
        BuildCurve(topCurve, false);
        BuildCurve(bottomCurve, true);
        return result;
    }

    // Outline of any shape (not only the current one), for ghosts and thumbnails.
    // Fills top/bottom with edge points in model space, tip to shoulder. The model itself isn't changed.
    public void SampleOutline(NoseShape s, int samples, Vector2[] top, Vector2[] bottom)
    {
        var saved = shape;
        shape = s;
        BuildCurve(topCurve, false);
        BuildCurve(bottomCurve, true);

        float L = NoseLength;
        for (int i = 0; i < samples; i++)
        {
            float t = i / (samples - 1f);
            float x = -L + L * t * t;
            float c = CenterAt(x);
            top[i] = new Vector2(x, c + RadiusAt(x, false));
            bottom[i] = new Vector2(x, c - RadiusAt(x, true));
        }

        shape = saved;
        BuildCurve(topCurve, false);
        BuildCurve(bottomCurve, true);
    }

    // Test mode: tint the surface blue (smooth) to red (turbulent), split at each side's transition point.
    // tTop / tBottom: 0 = tip, 1 = tail. amount: 0 = bare metal, 1 = full tint.
    public void PaintFlow(float tTop, float tBottom, float amount, bool marker, float time)
    {
        if (mesh == null || colors.Count != uvs.Count) return;
        for (int i = 0; i < uvs.Count; i++)
        {
            Vector2 uv = uvs[i];
            float side = 0.5f + 0.5f * Mathf.Cos(uv.y * 2f * Mathf.PI);   // 1 = top, 0 = bottom
            float t = Mathf.Lerp(tBottom, tTop, side);
            float k = Mathf.Clamp01((uv.x - t) / 0.05f);
            k = k * k * (3f - 2f * k);
            float churn = Mathf.PerlinNoise(uv.x * 40f - time * 8f, uv.y * 12f);
            Color flowColor = Color.Lerp(laminarPaint, turbulentPaint * (0.7f + 0.6f * churn), k);
            Color c = Color.Lerp(metalColor, flowColor, amount);
            if (marker && Mathf.Abs(uv.x - t) < 0.004f) c = Color.Lerp(c, Color.white * 1.5f, amount);
            colors[i] = c;
        }
        mesh.SetColors(colors);
    }

    public void ClearFlow()
    {
        for (int i = 0; i < colors.Count; i++) colors[i] = metalColor;
        if (mesh != null) mesh.SetColors(colors);
    }
    public void Rebuild()
    {
        shape ??= NoseShape.CleanBase();
        shape.Sanitize();
        BuildCurve(topCurve, false);
        BuildCurve(bottomCurve, true);
        BuildRings();

        verts.Clear(); normals.Clear(); uvs.Clear(); tris.Clear();
        AddSide();
        AddTailCap();

        colors.Clear();
        for (int i = 0; i < verts.Count; i++) colors.Add(metalColor);

        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(colors);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
    }

    void BuildCurve(SideCurve c, bool isBottom)
    {
        c.x.Clear(); c.s.Clear();
        for (int i = 0; i < ControlPointCount; i++)
        {
            Vector2 p = ControlPoint(i, isBottom);
            c.x.Add(p.x);
            c.s.Add(p.y * p.y);
        }
        if (c.m.Length != ControlPointCount) c.m = new float[ControlPointCount];
        NoseCurve.Slopes(c.x, c.s, c.m, 0f);
    }

    void BuildRings()
    {
        ringX.Clear(); ringTop.Clear(); ringBottom.Clear(); ringCenter.Clear();
        float L = NoseLength;
        for (int i = 0; i <= noseRings; i++)
        {
            float t = i / (float)noseRings;
            float x = -L + L * t * t; // bunched near the tip, where the curve bends hardest
            ringX.Add(x);
            ringTop.Add(RadiusAt(x, false));
            ringBottom.Add(RadiusAt(x, true));
            ringCenter.Add(CenterAt(x));
        }
        const int bodyRings = 16;
        for (int i = 1; i <= bodyRings; i++)
        {
            ringX.Add(bodyLength * i / bodyRings);
            ringTop.Add(bodyRadius);
            ringBottom.Add(bodyRadius);
            ringCenter.Add(0f);
        }
    }

    // Radius around a ring: the top curve at phi = 0, the bottom curve at phi = 180°, blended smoothly between.
    static float BlendRadius(float top, float bottom, float cosPhi) => Mathf.Lerp(bottom, top, 0.5f + 0.5f * cosPhi);

    void AddSide()
    {
        int rings = ringX.Count;
        int stride = segments + 1;
        float tipX = ringX[0];
        float total = bodyLength - tipX;

        for (int i = 0; i < rings; i++)
        {
            for (int j = 0; j <= segments; j++)
            {
                float phi = 2f * Mathf.PI * j / segments;
                float c = Mathf.Cos(phi), s = Mathf.Sin(phi);
                float r = BlendRadius(ringTop[i], ringBottom[i], c);
                verts.Add(new Vector3(ringX[i], ringCenter[i] + r * c, r * s));
                uvs.Add(new Vector2((ringX[i] - tipX) / total, j / (float)segments));
            }
        }

        // Normals from the neighbouring vertices: one direction along the model, one around it.
        Vector3 V(int i, int j) => verts[i * stride + j];
        for (int i = 0; i < rings; i++)
        {
            for (int j = 0; j <= segments; j++)
            {
                Vector3 along = V(Mathf.Min(i + 1, rings - 1), j) - V(Mathf.Max(i - 1, 0), j);
                Vector3 around = V(i, (j + 1) % segments) - V(i, (j - 1 + segments) % segments);
                normals.Add(Vector3.Cross(around, along).normalized);
            }
        }

        // The tip ring is a single point, so it has no "around" direction. Use the average of the next ring.
        Vector3 tipNormal = Vector3.zero;
        for (int j = 0; j < segments; j++) tipNormal += normals[stride + j];
        tipNormal.Normalize();
        for (int j = 0; j <= segments; j++) normals[j] = tipNormal;

        for (int i = 0; i < rings - 1; i++)
        {
            for (int j = 0; j < segments; j++)
            {
                int a = i * stride + j, a1 = a + 1, b = a + stride, b1 = b + 1;
                tris.Add(a);  tris.Add(a1); tris.Add(b);
                tris.Add(a1); tris.Add(b1); tris.Add(b);
            }
        }
    }

    void AddTailCap()
    {
        int start = verts.Count;
        int stride = segments + 1;
        for (int ring = 0; ring < 2; ring++)
        {
            float r = ring == 0 ? bodyRadius : 0f;
            for (int j = 0; j <= segments; j++)
            {
                float phi = 2f * Mathf.PI * j / segments;
                verts.Add(new Vector3(bodyLength, r * Mathf.Cos(phi), r * Mathf.Sin(phi)));
                normals.Add(Vector3.right);
                uvs.Add(new Vector2(1f, j / (float)segments));
            }
        }
        for (int j = 0; j < segments; j++)
        {
            int a = start + j, a1 = a + 1, b = a + stride, b1 = b + 1;
            tris.Add(a);  tris.Add(a1); tris.Add(b);
            tris.Add(a1); tris.Add(b1); tris.Add(b);
        }
    }

    void OnDrawGizmos()
    {
        if (ringX.Count < 2) return;
        Gizmos.matrix = transform.localToWorldMatrix;
        float z = -(bodyRadius + 0.02f);

        Gizmos.color = new Color(1f, 1f, 1f, 0.5f);
        for (int i = 0; i < noseRings && i + 1 < ringX.Count; i++)
        {
            float c0 = ringCenter[i], c1 = ringCenter[i + 1];
            Gizmos.DrawLine(new Vector3(ringX[i], c0 + ringTop[i], z), new Vector3(ringX[i + 1], c1 + ringTop[i + 1], z));
            Gizmos.DrawLine(new Vector3(ringX[i], c0 - ringBottom[i], z), new Vector3(ringX[i + 1], c1 - ringBottom[i + 1], z));
        }

        float size = bodyRadius * 0.07f;
        int last = ControlPointCount - 1;
        for (int i = 0; i <= last; i++)
        {
            Gizmos.color = i == 0 ? Color.yellow : i == last ? Color.gray : Color.cyan;
            Vector2 t = DotPosition(i, false);
            Gizmos.DrawSphere(new Vector3(t.x, t.y, z), size);
            if (i == 0) continue;
            Vector2 b = DotPosition(i, true);
            Gizmos.DrawSphere(new Vector3(b.x, b.y, z), size);
        }
    }
}