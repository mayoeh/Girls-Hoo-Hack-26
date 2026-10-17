using System.Collections.Generic;
using UnityEngine;

// Monotone cubic curve through points (x[i], y[i]).
// Slopes: a parabola through each point and its neighbours (exact for parabolas, so a hemisphere is reproduced exactly),
// then the Fritsch–Carlson limiter so the curve never overshoots between points.
public static class NoseCurve
{
    public static void Slopes(List<float> x, List<float> y, float[] m, float endSlope)
    {
        int n = x.Count;                       // needs at least 3 points
        var h = new float[n - 1];
        var d = new float[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            h[i] = x[i + 1] - x[i];
            d[i] = (y[i + 1] - y[i]) / h[i];
        }

        for (int i = 1; i < n - 1; i++)
            m[i] = (h[i] * d[i - 1] + h[i - 1] * d[i]) / (h[i - 1] + h[i]);
        m[0] = ((2f * h[0] + h[1]) * d[0] - h[0] * d[1]) / (h[0] + h[1]);
        m[n - 1] = endSlope;                   // 0 at the shoulder = flat blend into the body

        for (int i = 0; i < n - 1; i++)
        {
            if (Mathf.Abs(d[i]) < 1e-7f) { m[i] = 0f; m[i + 1] = 0f; continue; }
            if (m[i] * d[i] < 0f) m[i] = 0f;
            if (m[i + 1] * d[i] < 0f) m[i + 1] = 0f;
            float a = m[i] / d[i], b = m[i + 1] / d[i];
            float t = a * a + b * b;
            if (t > 9f)
            {
                float k = 3f / Mathf.Sqrt(t);
                m[i] = k * a * d[i];
                m[i + 1] = k * b * d[i];
            }
        }
    }

    public static float Evaluate(List<float> x, List<float> y, float[] m, float xq)
    {
        int n = x.Count;
        if (xq <= x[0]) return y[0];
        if (xq >= x[n - 1]) return y[n - 1];
        int i = 0;
        while (xq > x[i + 1]) i++;
        float h = x[i + 1] - x[i];
        float t = (xq - x[i]) / h;
        float t2 = t * t, t3 = t2 * t;
        return (2f * t3 - 3f * t2 + 1f) * y[i]
             + (t3 - 2f * t2 + t) * h * m[i]
             + (-2f * t3 + 3f * t2) * y[i + 1]
             + (t3 - t2) * h * m[i + 1];
    }
}