using UnityEngine;

public static class FlowModel
{
    public static float Transition(float noseAngle, float bluntness, float tipBend, float mach, float variation)
    {
        float angle = Mathf.Clamp(noseAngle, 10f, 60f);
        float lowSpeed = Mathf.Lerp(1f, 0.3f, (mach - 1.6f) / 0.6f);
        float t = 0.85f
                - 0.10f * (angle - 10f) / 50f              // wider angle: slightly earlier
                - 0.45f * bluntness * lowSpeed             // blunter: much earlier, especially at low speed
                - 0.003f * Mathf.Abs(tipBend)              // game rule, not from her reports: a bent tip trips the flow sooner
                + variation;
        return Mathf.Clamp(t, 0.10f, 0.95f);
    }

    public struct Shock
    {
        public bool exists;
        public float vertexX, vertexY;   // the shock's front point, in model space
        public float tanBeta;            // how steeply it spreads
        public float curvature;          // how rounded its front is (blunt noses = rounder, further ahead)
    }

    public static Shock ShockFor(float halfAngleDeg, float tipRadius, float tipX, float tipY, float mach, float bodyRadius)
    {
        var s = new Shock();
        if (mach <= 1.02f) return s;                                   // no shock below the speed of sound
        float mu = Mathf.Asin(1f / mach);
        float beta = Mathf.Clamp(halfAngleDeg * Mathf.Deg2Rad + 0.7f * mu, mu + 0.01f, 80f * Mathf.Deg2Rad);
        float detach = Mathf.Max(0f, halfAngleDeg - 32f) * 0.02f * bodyRadius;   // very wide noses push it forward

        s.exists = true;
        s.vertexX = tipX - (0.3f * tipRadius + detach);
        s.vertexY = tipY;
        s.tanBeta = Mathf.Tan(beta);
        s.curvature = Mathf.Max(1.4f * tipRadius + 2f * detach, 0.01f * bodyRadius);
        return s;
    }

    public static float ShockRadius(in Shock s, float x)
    {
        float d = x - s.vertexX;
        if (d < 0f) return -1f;
        return Mathf.Sqrt(d * d * s.tanBeta * s.tanBeta + 2f * s.curvature * d);
    }
}