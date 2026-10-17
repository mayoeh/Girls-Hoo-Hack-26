using UnityEngine;

public struct SideMetrics
{
    public float noseAngle;   // full cone angle in degrees, from the taper between 25% and 75% of the nose length
    public float bluntness;   // tip radius ÷ body radius: 0 = sharp, 1 = hemisphere
}

public struct ShapeMetrics
{
    public SideMetrics top, bottom;
    public bool mirrored;
    public float noseLength;  // in body radii
    public float fineness;    // nose length ÷ body diameter
    public float fullness;    // nose volume ÷ volume of a cylinder the same length
    public float tipBend;     // degrees the tip points up (+) or down (-)

    // The game uses the worse side: the wider, blunter one trips the flow first.
    public float Angle => Mathf.Max(top.noseAngle, bottom.noseAngle);
    public float Bluntness => Mathf.Max(top.bluntness, bottom.bluntness);
    public bool InTestedRange => Angle >= 10f && Angle <= 60f;   // Mary Jackson's tested cone angles

    // The GDD transition rule, without the random run-to-run variation.
    public float PredictedSmoothFlow(float mach)
    {
        float angle = Mathf.Clamp(Angle, 10f, 60f);
        float lowSpeed = Mathf.Lerp(1f, 0.3f, (mach - 1.6f) / 0.6f);
        float t = 0.85f - 0.10f * (angle - 10f) / 50f - 0.45f * Bluntness * lowSpeed;
        return Mathf.Clamp(t, 0.10f, 0.95f);
    }

    public static ShapeMetrics Measure(RocketModel m)
    {
        float R = m.bodyRadius, L = m.NoseLength;
        var r = new ShapeMetrics
        {
            mirrored = m.shape.mirrored,
            noseLength = m.shape.length,
            fineness = m.shape.length / 2f,
            tipBend = Mathf.Atan(2f * m.shape.tipHeight * R / L) * Mathf.Rad2Deg,
            top = MeasureSide(m, false),
        };
        r.bottom = r.mirrored ? r.top : MeasureSide(m, true);

        // Fullness: add up thin slices of the nose. Each slice's area accounts for top and bottom being different.
        const int steps = 200;
        float dx = L / steps, volume = 0f;
        for (int i = 0; i < steps; i++)
        {
            float x = -L + dx * (i + 0.5f);
            float rt = m.RadiusAt(x, false), rb = m.RadiusAt(x, true);
            float a = 0.5f * (rt + rb), b = 0.5f * (rt - rb);
            volume += Mathf.PI * (a * a + 0.5f * b * b) * dx;
        }
        r.fullness = volume / (Mathf.PI * R * R * L);
        return r;
    }

    static SideMetrics MeasureSide(RocketModel m, bool isBottom)
    {
        float R = m.bodyRadius, L = m.NoseLength;
        float x1 = -0.75f * L, x2 = -0.25f * L;               // 25% and 75% of the way from tip to shoulder
        float slope = (m.RadiusAt(x2, isBottom) - m.RadiusAt(x1, isBottom)) / (x2 - x1);
        return new SideMetrics
        {
            noseAngle = 2f * Mathf.Atan(Mathf.Max(0f, slope)) * Mathf.Rad2Deg,
            bluntness = Mathf.Clamp01(m.TipSlope(isBottom) * 0.5f / R),
        };
    }
}