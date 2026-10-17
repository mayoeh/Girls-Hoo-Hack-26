using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class FlowLines : MonoBehaviour
{
    public RocketModel rocket;
    [Tooltip("URP Particles/Unlit material, Transparent + Additive.")]
    public Material lineMaterial;

    [Header("Lines")]
    [Tooltip("Height of each smoke line above the surface, in body radii. One line per value on each side.")]
    public float[] heights = { 0.06f, 0.14f, 0.24f, 0.36f, 0.5f, 0.68f, 0.9f, 1.15f, 1.45f, 1.8f };
    public int pointsPerLine = 140;
    public float lineWidth = 0.006f;
    [Tooltip("How far the lines reach in front of the tip and behind the tail (world units).")]
    public float upstream = 0.6f, downstream = 0.9f;
    public float flowSpeed = 2.5f;
    public float dashesPerUnit = 5f;
    [Tooltip("Lines closer than this to the surface (body radii) go turbulent past the transition point.")]
    public float boundaryLayer = 0.45f;
    [Tooltip("Tunnel walls, in body radii from the centre line.")]
    public float wallHeight = 2.4f;

    [Header("Colours")]
    public Color laminar = new(0.35f, 0.75f, 1f);
    public Color turbulent = new(1f, 0.4f, 0.15f);
    public Color outer = new(0.55f, 0.7f, 0.9f);
    public Color shockColor = new(0.9f, 0.95f, 1f);

    [Header("Tracers (moving glowing streaks)")]
    public int tracerCount = 420;
    public int trailPoints = 14;
    [Tooltip("Streak length at free-stream speed (world units). Streaks stretch when air is fast and shrink where it slows.")]
    public float trailLength = 0.26f;
    public float tracerWidth = 0.008f;
    [Tooltip("How fast tracers travel at full tunnel speed (world units per second).")]
    public float tracerSpeed = 1.4f;
    public Color fastColor = new(0.8f, 0.96f, 1f);
    public Color slowColor = new(0.15f, 0.4f, 1f);
    [Tooltip("Brightness of the fixed smoke lines behind the tracers (0 hides them).")]
    [Range(0f, 1f)] public float guideLineAlpha = 0.3f;

    struct Tracer { public float x, hR, seed, jitter; public int sign; }
    Tracer[] tracers;
    Mesh tracerMesh;
    MeshRenderer tracerRenderer;
    Vector3[] tracerVerts;
    Color[] tracerColors;
    Vector2[] trail;

    readonly List<LineRenderer> lines = new();
    LineRenderer shockTop, shockBottom;
    Material lineMat, shockMat;
    Vector3[] points;
    readonly Vector3[] shockPoints = new Vector3[48];
    float scroll;
    readonly Gradient gradient = new();
    readonly GradientColorKey[] colorKeys = new GradientColorKey[4];
    readonly GradientAlphaKey[] alphaKeys = new GradientAlphaKey[4];

    // Called every frame by the test bench.
    public void Render(ShapeMetrics m, float mach, float speed01, float tTop, float tBottom, float dt)
    {
        if (lines.Count == 0) Build();

        float alpha = Mathf.Clamp01(speed01 * 1.5f);
        bool visible = alpha > 0.01f;
        foreach (var lr in lines) lr.enabled = visible && guideLineAlpha > 0.001f;
        tracerRenderer.enabled = visible;
        if (!visible) { shockTop.enabled = shockBottom.enabled = false; return; }

        float R = rocket.bodyRadius, L = rocket.NoseLength;
        float tipX = -L, tipY = rocket.CenterAt(-L);
        float xStart = tipX - upstream, xEnd = rocket.bodyLength + downstream;
        float modelLength = rocket.bodyLength - tipX;
        float z = -(R + 0.05f);                                    // just in front of the model, facing the camera
        float wall = wallHeight * R;
        float time = Time.time;
        float develop = Mathf.Clamp01((speed01 - 0.35f) / 0.65f);  // turbulence builds as the tunnel reaches speed

        var shTop = FlowModel.ShockFor(m.top.noseAngle * 0.5f, m.top.bluntness * R, tipX, tipY, mach, R);
        var shBottom = FlowModel.ShockFor(m.bottom.noseAngle * 0.5f, m.bottom.bluntness * R, tipX, tipY, mach, R);

        scroll += dt * flowSpeed * speed01;
        lineMat.SetTextureOffset("_BaseMap", new Vector2(-scroll * dashesPerUnit, 0f));

        int n = pointsPerLine;
        for (int side = 0; side < 2; side++)
        {
            int sign = side == 0 ? 1 : -1;
            var shock = side == 0 ? shTop : shBottom;
            float xT = tipX + (side == 0 ? tTop : tBottom) * modelLength;

            for (int k = 0; k < heights.Length; k++)
            {
                var lr = lines[side * heights.Length + k];
                float h = heights[k] * R;
                float closeness = Mathf.Clamp01(1f - heights[k] / boundaryLayer);   // 1 at the surface, 0 outside the boundary layer

                for (int i = 0; i < n; i++)
                {
                    float x = Mathf.Lerp(xStart, xEnd, i / (n - 1f));
                    float y = StreamY(x, h, sign, shock, tipY, R);

                    float turb = Turbulence(x, heights[k], xT, develop, R);
                    if (turb > 0f) y = Churn(x, y, sign, turb, xT, modelLength, R, time, k * 3.7f + side * 50f);

                    y = Mathf.Clamp(y, -wall + 0.01f, wall - 0.01f);
                    points[i] = rocket.transform.TransformPoint(new Vector3(x, y, z));
                }
                lr.SetPositions(points);

                float tf = Mathf.Clamp01((xT - xStart) / (xEnd - xStart));
                bool near = closeness > 0f;
                Color a = near ? laminar : outer;
                Color b = near ? turbulent : outer;
                float lineAlpha = alpha * guideLineAlpha * (near ? 0.9f : 0.4f);
                colorKeys[0] = new GradientColorKey(a, 0f);
                colorKeys[1] = new GradientColorKey(a, tf);
                colorKeys[2] = new GradientColorKey(b, Mathf.Min(1f, tf + 0.05f));
                colorKeys[3] = new GradientColorKey(b, 1f);
                alphaKeys[0] = new GradientAlphaKey(0f, 0f);
                alphaKeys[1] = new GradientAlphaKey(lineAlpha, 0.05f);
                alphaKeys[2] = new GradientAlphaKey(lineAlpha, 0.95f);
                alphaKeys[3] = new GradientAlphaKey(0f, 1f);
                gradient.SetKeys(colorKeys, alphaKeys);
                lr.colorGradient = gradient;
            }
        }

        RenderTracers(shTop, shBottom, tipX, tipY, xStart, xEnd, modelLength, tTop, tBottom, z, wall, alpha, develop, speed01, dt, R);

        // Schlieren-style shimmer on the shock.
        float shimmer = 0.75f + 0.25f * Mathf.PerlinNoise(time * 11f, 0.3f);
        float shockAlpha = Mathf.Clamp01((mach - 1.02f) / 0.3f) * alpha * shimmer;
        DrawShock(shockTop, shTop, 1, wall, z, xEnd, shockAlpha);
        DrawShock(shockBottom, shBottom, -1, wall, z, xEnd, shockAlpha);
    }

    public void Hide()
    {
        foreach (var lr in lines) lr.enabled = false;
        if (shockTop) shockTop.enabled = false;
        if (shockBottom) shockBottom.enabled = false;
        if (tracerRenderer) tracerRenderer.enabled = false;
    }

    // ---------- turbulence (shared by the smoke lines and the tracers) ----------

    // 0 = smooth, 1 = fully turbulent. hR = height above the surface in body radii.
    float Turbulence(float x, float hR, float xT, float develop, float R)
    {
        float closeness = Mathf.Clamp01(1f - hR / boundaryLayer);   // 1 at the surface, 0 outside the boundary layer
        if (closeness <= 0f) return 0f;
        float turb = 0f;
        if (x > xT) turb = Mathf.Clamp01((x - xT) / (0.5f * R)) * closeness;
        if (x > rocket.bodyLength)   // the wake behind the tail is always churned up
            turb = Mathf.Max(turb, 0.7f * closeness * Mathf.Clamp01((x - rocket.bodyLength) / (0.3f * R)));
        return turb * develop;
    }

    // Moves a point by the turbulent wobble, keeping it outside the surface.
    float Churn(float x, float y, int sign, float turb, float xT, float modelLength, float R, float time, float seed)
    {
        float noise = Mathf.PerlinNoise(x * 14f - time * 7f, seed) * 2f - 1f;
        float grow = 0.12f + 0.3f * Mathf.Clamp01((x - xT) / modelLength);
        y += sign * turb * R * grow * (noise + 0.6f);                 // the turbulent layer thickens and wobbles
        float surface = rocket.CenterAt(x) + sign * EffectiveRadius(x, sign);
        return sign > 0 ? Mathf.Max(y, surface + 0.004f) : Mathf.Min(y, surface - 0.004f);
    }

    // ---------- tracers ----------

    // Rough air speed (1 = free stream): slows approaching the nose, most for streaks aimed at the tip,
    // a little slower behind the shock, and slower again in the turbulent layer.
    float LocalSpeed(float x, float hR, float tipX, float R, bool behindShock, float turb)
    {
        float d = (x - tipX) / (0.45f * R);
        float aimedAtTip = Mathf.Exp(-hR * 2.5f);
        float v = 1f - 0.75f * aimedAtTip * Mathf.Exp(-d * d);
        if (behindShock) v *= 0.88f;
        v *= Mathf.Lerp(1f, 0.55f, turb);
        return Mathf.Clamp(v, 0.15f, 1.1f);
    }

    void RenderTracers(FlowModel.Shock shTop, FlowModel.Shock shBottom, float tipX, float tipY, float xStart, float xEnd,
                       float modelLength, float tTop, float tBottom, float z, float wall, float alpha, float develop,
                       float speed01, float dt, float R)
    {
        float time = Time.time;
        int K = trailPoints;
        Transform rt = rocket.transform;

        for (int i = 0; i < tracers.Length; i++)
        {
            ref Tracer tr = ref tracers[i];
            var shock = tr.sign > 0 ? shTop : shBottom;
            float xT = tipX + (tr.sign > 0 ? tTop : tBottom) * modelLength;
            float h = tr.hR * R;

            // Move along the streamline at the local air speed.
            float turbHead = Turbulence(tr.x, tr.hR, xT, develop, R);
            bool behind = shock.exists && FlowModel.ShockRadius(shock, tr.x) > h;
            float v = LocalSpeed(tr.x, tr.hR, tipX, R, behind, turbHead);
            tr.x += tracerSpeed * speed01 * v * tr.jitter * dt;
            if (tr.x > xEnd) { tr.x = xStart - Random.value * 0.3f; tr.seed = Random.value * 100f; }

            // Trail: points behind the head along the same streamline. Fast air = long streak, slow air = short.
            float len = trailLength * Mathf.Lerp(0.35f, 1f, v);
            for (int j = 0; j < K; j++)
            {
                float x = tr.x - len * j / (K - 1f);
                float y = StreamY(x, h, tr.sign, shock, tipY, R);
                float turb = Turbulence(x, tr.hR, xT, develop, R);
                if (turb > 0f) y = Churn(x, y, tr.sign, turb, xT, modelLength, R, time, tr.seed);
                trail[j] = new Vector2(x, Mathf.Clamp(y, -wall + 0.01f, wall - 0.01f));
            }

            // Colour: deep blue where the air is slow, bright cyan-white where it's fast, orange when turbulent.
            Color head = Color.Lerp(slowColor, fastColor, Mathf.InverseLerp(0.3f, 1f, v));
            head = Color.Lerp(head, turbulent, turbHead);
            float edge = Mathf.Clamp01((tr.x - xStart) / 0.3f) * Mathf.Clamp01((xEnd - tr.x) / 0.3f);

            int vb = i * K * 2;
            for (int j = 0; j < K; j++)
            {
                Vector2 a = trail[Mathf.Max(j - 1, 0)], b = trail[Mathf.Min(j + 1, K - 1)];
                Vector2 dir = (a - b).normalized;
                Vector2 normal = new(-dir.y, dir.x);
                float f = j / (K - 1f);                                   // 0 at the head, 1 at the tail
                float halfWidth = tracerWidth * 0.5f * (1f - 0.7f * f);
                Vector2 p = trail[j];
                tracerVerts[vb + 2 * j]     = rt.TransformPoint(new Vector3(p.x + normal.x * halfWidth, p.y + normal.y * halfWidth, z));
                tracerVerts[vb + 2 * j + 1] = rt.TransformPoint(new Vector3(p.x - normal.x * halfWidth, p.y - normal.y * halfWidth, z));
                Color c = head;
                c.a = alpha * edge * (1f - f) * (1f - f);
                tracerColors[vb + 2 * j] = tracerColors[vb + 2 * j + 1] = c;
            }
        }

        tracerMesh.vertices = tracerVerts;
        tracerMesh.colors = tracerColors;
    }

    void BuildTracers()
    {
        tracers = new Tracer[tracerCount];
        trail = new Vector2[trailPoints];
        float x0 = -rocket.NoseLength - upstream, x1 = rocket.bodyLength + downstream;
        for (int i = 0; i < tracers.Length; i++)
        {
            tracers[i] = new Tracer
            {
                sign = i % 2 == 0 ? 1 : -1,
                hR = Mathf.Lerp(0.03f, 1.9f, Mathf.Pow(Random.value, 1.7f)),   // more streaks close to the surface
                x = Mathf.Lerp(x0, x1, Random.value),
                seed = Random.value * 100f,
                jitter = Random.Range(0.85f, 1.15f),
            };
        }

        int K = trailPoints, quads = K - 1;
        tracerVerts = new Vector3[tracerCount * K * 2];
        tracerColors = new Color[tracerVerts.Length];
        var tris = new int[tracerCount * quads * 6];
        int t = 0;
        for (int i = 0; i < tracerCount; i++)
        {
            int vb = i * K * 2;
            for (int j = 0; j < quads; j++)
            {
                int a = vb + 2 * j;
                tris[t++] = a; tris[t++] = a + 1; tris[t++] = a + 2;      // clockwise as seen from the camera
                tris[t++] = a + 1; tris[t++] = a + 3; tris[t++] = a + 2;
            }
        }

        tracerMesh = new Mesh { name = "Flow tracers" };
        tracerMesh.MarkDynamic();
        tracerMesh.vertices = tracerVerts;
        tracerMesh.colors = tracerColors;
        tracerMesh.triangles = tris;
        tracerMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100f);   // never culled

        var go = new GameObject("Flow Tracers");                          // world-space mesh, kept at the origin
        go.AddComponent<MeshFilter>().sharedMesh = tracerMesh;
        tracerRenderer = go.AddComponent<MeshRenderer>();
        tracerRenderer.sharedMaterial = shockMat;                           // plain additive glow, no dashes
        tracerRenderer.shadowCastingMode = ShadowCastingMode.Off;
        tracerRenderer.receiveShadows = false;
        tracerRenderer.enabled = false;
    }
    float StreamY(float x, float h, int sign, FlowModel.Shock shock, float tipY, float R)
    {
        float r = EffectiveRadius(x, sign);
        float inside = rocket.CenterAt(x) + sign * Mathf.Sqrt(r * r + h * h);
        if (!shock.exists) return inside;

        float free = tipY + sign * h;
        float rs = FlowModel.ShockRadius(shock, x);
        if (rs < 0f) return free;
        float k = Mathf.Clamp01((rs - h) / (0.12f * R));   // turns sharply where it crosses the shock
        return Mathf.Lerp(free, inside, k);
    }

    float EffectiveRadius(float x, int sign)
    {
        float R = rocket.bodyRadius;
        if (x <= -rocket.NoseLength) return 0f;
        if (x <= rocket.bodyLength) return rocket.RadiusAt(x, sign < 0);
        float d = x - rocket.bodyLength;
        return Mathf.Max(0.25f * R, R * Mathf.Exp(-d / (1.2f * R)));
    }

    void DrawShock(LineRenderer lr, FlowModel.Shock s, int sign, float wall, float z, float xEnd, float alpha)
    {
        lr.enabled = s.exists && alpha > 0.01f;
        if (!lr.enabled) return;

        float t2 = s.tanBeta * s.tanBeta, rc = s.curvature;
        float room = Mathf.Max(0.01f, wall - sign * s.vertexY);           // distance to the wall on this side
        float dMax = Mathf.Min(xEnd - s.vertexX, (-rc + Mathf.Sqrt(rc * rc + t2 * room * room)) / t2);
        int n = shockPoints.Length;
        for (int i = 0; i < n; i++)
        {
            float d = dMax * Mathf.Pow(i / (n - 1f), 1.6f);               // more points near the curved front
            float r = Mathf.Sqrt(d * d * t2 + 2f * rc * d);
            shockPoints[i] = rocket.transform.TransformPoint(new Vector3(s.vertexX + d, s.vertexY + sign * r, z));
        }
        lr.SetPositions(shockPoints);

        Color c = shockColor;
        c.a = alpha;
        lr.startColor = c;
        c.a = alpha * 0.3f;
        lr.endColor = c;
    }

    void Build()
    {
        points = new Vector3[pointsPerLine];
        lineMat = new Material(lineMaterial);
        lineMat.SetTexture("_BaseMap", MakeDashTexture());
        shockMat = new Material(lineMaterial);
        shockMat.SetTexture("_BaseMap", Texture2D.whiteTexture);

        for (int side = 0; side < 2; side++)
            for (int k = 0; k < heights.Length; k++)
                lines.Add(MakeLine($"Flow {(side == 0 ? "top" : "bottom")} {k}", lineMat, pointsPerLine, true));

        shockTop = MakeLine("Shock top", shockMat, shockPoints.Length, false);
        shockBottom = MakeLine("Shock bottom", shockMat, shockPoints.Length, false);
        shockTop.widthMultiplier = shockBottom.widthMultiplier = lineWidth * 1.6f;
        BuildTracers();
    }

    LineRenderer MakeLine(string name, Material mat, int count, bool tiled)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = mat;
        lr.useWorldSpace = true;
        lr.positionCount = count;
        lr.widthMultiplier = lineWidth;
        lr.textureMode = tiled ? LineTextureMode.Tile : LineTextureMode.Stretch;
        if (tiled) lr.textureScale = new Vector2(dashesPerUnit, 1f);
        lr.shadowCastingMode = ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.enabled = false;
        return lr;
    }

    static Texture2D MakeDashTexture()
    {
        const int w = 64;
        var tex = new Texture2D(w, 4, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
        };
        for (int x = 0; x < w; x++)
        {
            float u = x / (float)w;
            float pulse = u < 0.6f ? Mathf.Pow(u / 0.6f, 2f) : Mathf.Max(0f, 1f - (u - 0.6f) / 0.06f);
            float a = 0.15f + 0.85f * pulse;
            for (int y = 0; y < 4; y++) tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();
        return tex;
    }
}