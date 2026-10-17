using UnityEngine;
using TMPro;

public class StarBreathingGlow : MonoBehaviour
{
    [Header("Material Settings")]
    [ColorUsage(true, true)] public Color baseGlowColor = Color.cyan;

    [Header("Breathing Intensity Bounds")]
    public float minIntensity = 0.5f; // Dim state (Inhale)
    public float maxIntensity = 4.0f; // Bright state (Exhale)

    [Header("UI Confirmation")]
    public TextMeshProUGUI statusText;

    [Header("Sensitivity & Noise Filtering")]
    [Tooltip("Smoothing applied to raw input frames (higher = smoother, slower)")]
    public float smoothingSpeed = 3.0f;
    [Tooltip("Sensitivity to detect trend direction")]
    public float sensitivity = 0.005f;

    [Header("Test Mode")]
    public bool useAutoPulseTest = false;
    [Range(0f, 1f)] public float testBreathingInput = 0.5f;

    private Material objectMaterial;
    private float smoothedInputValue = 0.5f;
    private float baselineValue = 0.5f;
    private bool hasReceivedInput = false;
    private static readonly int EmissionColorID = Shader.PropertyToID("_EmissionColor");

    void Start()
    {
        Renderer rend = GetComponent<Renderer>();
        if (rend != null)
        {
            objectMaterial = rend.material;
            objectMaterial.EnableKeyword("_EMISSION");
        }

        if (statusText != null)
        {
            statusText.text = "Waiting for breathing input...";
            statusText.color = Color.white;
        }

        ApplyGlow(0.5f);
    }

    void Update()
    {
        if (useAutoPulseTest)
        {
            float pulse = (Mathf.Sin(Time.time * 2.5f) + 1.0f) / 2.0f;
            UpdateGlowFromBreathing(pulse);
        }
    }

    public void UpdateGlowFromBreathing(float rawValue)
    {
        float targetValue = Mathf.Clamp01(rawValue);

        // Exponential smoothing to strip high-frequency webcam noise
        smoothedInputValue = Mathf.Lerp(smoothedInputValue, targetValue, Time.deltaTime * smoothingSpeed);

        // Slow-moving baseline adjustment to adapt to drift
        baselineValue = Mathf.Lerp(baselineValue, smoothedInputValue, Time.deltaTime * 0.5f);

        // Difference from rolling baseline
        float diff = smoothedInputValue - baselineValue;

        if (statusText != null)
        {
            if (diff > sensitivity)
            {
                statusText.text = "Breathing In...";
                statusText.color = Color.white;
                hasReceivedInput = true;
            }
            else if (diff < -sensitivity)
            {
                statusText.text = "Breathing Out...";
                statusText.color = Color.cyan;
                hasReceivedInput = true;
            }
            else if (!hasReceivedInput)
            {
                statusText.text = "Waiting for breathing input...";
                statusText.color = Color.gray;
            }
        }

        ApplyGlow(smoothedInputValue);
    }

    private void ApplyGlow(float t)
    {
        // Dim on Inhale (t = 1.0), Glow on Exhale (t = 0.0)
        float currentIntensity = Mathf.Lerp(maxIntensity, minIntensity, t);
        
        if (objectMaterial != null)
        {
            Color finalColor = baseGlowColor * Mathf.Pow(2, currentIntensity);
            objectMaterial.SetColor(EmissionColorID, finalColor);
        }
    }
}