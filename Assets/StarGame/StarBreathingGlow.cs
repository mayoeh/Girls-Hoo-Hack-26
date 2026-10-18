using UnityEngine;
using TMPro;

public class StarBreathingGlow : MonoBehaviour
{
    [Header("Material Settings")]
    [ColorUsage(true, true)] public Color baseGlowColor = Color.cyan;

    [Header("Breathing Intensity Bounds")]
    public float minIntensity = 0.5f; // Dim state (fully breathed out)
    public float maxIntensity = 4.0f; // Bright state (fully breathed in)

    [Header("UI Confirmation")]
    public TextMeshProUGUI statusText;

    [Header("Sensitivity & Noise Filtering")]
    [Tooltip("Smoothing applied to raw input frames (higher = smoother, slower)")]
    public float smoothingSpeed = 3.0f;
    [Tooltip("Sensitivity to detect trend direction")]
    public float sensitivity = 0.005f;

    [Header("Auto Pulse (guide star)")]
    [Tooltip("When on, the star pulses by itself at the pace below. Used for the computer's guide star.")]
    public bool useAutoPulseTest = false;
    [Tooltip("Breaths per minute for the auto pulse. One full dim+bright cycle = one breath.")]
    public float pulseBreathsPerMinute = 8f;
    [Range(0f, 1f)] public float testBreathingInput = 0.5f;

    private Material objectMaterial;
    private float smoothedInputValue = 0.5f;
    private float baselineValue = 0.5f;
    private bool hasReceivedInput = false;
    private float pulsePhase;
    private static readonly int EmissionColorID = Shader.PropertyToID("_EmissionColor");

    /// <summary>0 = breathed out (dim), 1 = breathed in (bright).</summary>
    public float CurrentLevel => smoothedInputValue;

    // Auto-pulse timing, used by the on-screen breathing coach.
    /// <summary>True while the auto pulse is in its "breathe in" (getting brighter) half.</summary>
    public bool PulseInhaling => Mathf.Repeat(pulsePhase, Mathf.PI * 2f) < Mathf.PI;
    /// <summary>0..1 progress through the current in or out half.</summary>
    public float PulseHalfProgress => Mathf.Repeat(pulsePhase, Mathf.PI) / Mathf.PI;
    /// <summary>Seconds in one in (or out) half of a breath.</summary>
    public float PulseHalfSeconds => 30f / Mathf.Max(0.5f, pulseBreathsPerMinute);

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
            // Integrate phase so changing the pace mid-game doesn't make the star jump.
            pulsePhase = Mathf.Repeat(pulsePhase + Time.deltaTime * Mathf.Max(0.5f, pulseBreathsPerMinute) / 60f * Mathf.PI * 2f, Mathf.PI * 2f);
            float pulse = (Mathf.Sin(pulsePhase - Mathf.PI / 2f) + 1.0f) / 2.0f;
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
        // Brighter as you breathe in (t = 1), dimmer as you breathe out (t = 0)
        float currentIntensity = Mathf.Lerp(minIntensity, maxIntensity, t);

        if (objectMaterial != null)
        {
            Color finalColor = baseGlowColor * Mathf.Pow(2, currentIntensity);
            objectMaterial.SetColor(EmissionColorID, finalColor);
        }
    }
}
