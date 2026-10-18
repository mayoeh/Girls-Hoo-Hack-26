using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;

// Receives breathing values and status messages from breathing_udp_sender.exe
// and shows what is happening as text on screen (top-left).
//
// Messages from the sender:
//   "0.123456"          a breathing value
//   "STATUS:code|hint"  a validation or status message from Presage
//   "PING"              heartbeat, once per second
public class BreathingUdpReceiver : MonoBehaviour
{
    [Header("References")]
    public StarBreathingGlow starGlowScript;

    [Header("Network")]
    public int port = 5005;

    [Header("On-screen messages")]
    [Tooltip("Optional. Use a SEPARATE TextMeshPro text, not the one on StarBreathingGlow. " +
             "If left empty, the message is drawn with OnGUI instead.")]
    public TextMeshProUGUI messageText;
    public int guiFontSize = 28;
    [Tooltip("Seconds without any packet before we say the sender was lost.")]
    public float senderTimeoutSeconds = 3f;
    [Tooltip("Seconds a value counts as 'fresh' for the breathing in/out message.")]
    public float valueFreshSeconds = 2f;
    [Tooltip("Seconds a warning stays on screen after Presage last reported it.")]
    public float problemHoldSeconds = 2.5f;

    [Header("Breathing direction")]
    [Tooltip("Turn on if the text says 'in' while you are breathing out.")]
    public bool invertDirection = false;
    [Tooltip("How big a change (as a fraction of the recent range) counts as in or out.")]
    public float directionThreshold = 0.03f;

    [Header("Glow input")]
    [Tooltip("Rescale values to 0-1 using a rolling min/max. Keep ON for upper_trace / lower_trace.")]
    public bool normalizeForGlow = true;

    private readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
    private UdpClient client;
    private Thread thread;
    private volatile bool running;
    private string bindError;

    // Main-thread state
    private struct Problem { public string hint; public float time; }
    private readonly Dictionary<string, Problem> problems = new Dictionary<string, Problem>();
    private string infoText = "";
    private string errorText = "";
    private float lastOkTime = -999f;
    private float lastValueTime = -999f;
    private float lastPacketTime = -999f;
    private bool anyPacket;

    // Direction tracking
    private bool seeded;
    private float fast, slow, rollMin, rollMax;
    private int directionSign; // +1 rising, -1 falling, 0 unknown

    private string mainMessage = "Waiting for sender program (breathing_udp_sender.exe)...";
    private string warningMessage = "";
    private Color mainColor = Color.gray;
    private GUIStyle guiStyle;

    static readonly Color Red = new Color(1f, 0.35f, 0.35f);
    static readonly Color Orange = new Color(1f, 0.65f, 0.2f);

    void Start()
    {
        if (starGlowScript != null) starGlowScript.useAutoPulseTest = false;

        try
        {
            client = new UdpClient(port);
            running = true;
            thread = new Thread(Listen) { IsBackground = true };
            thread.Start();
        }
        catch (Exception e)
        {
            running = false;
            bindError = "Could not open UDP port " + port +
                        " (is another program using it? Try restarting Unity).";
            Debug.LogError("BreathingUdpReceiver: " + e.Message);
        }
    }

    void Listen()
    {
        var ep = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                byte[] data = client.Receive(ref ep);
                inbox.Enqueue(Encoding.UTF8.GetString(data).Trim());
            }
            catch (SocketException)
            {
                if (!running) break;
                Thread.Sleep(50);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    void Update()
    {
        while (inbox.TryDequeue(out string msg)) Handle(msg);
        RefreshDisplay();
    }

    void Handle(string msg)
    {
        lastPacketTime = Time.unscaledTime;
        anyPacket = true;

        if (msg == "PING") return;

        if (msg.StartsWith("STATUS:", StringComparison.Ordinal))
        {
            HandleStatus(msg.Substring(7).Trim());
            return;
        }

        if (float.TryParse(msg, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
        {
            HandleValue(v);
        }
    }

    void HandleStatus(string body)
    {
        // Format: "code|hint", or just free text for sender messages.
        string code = "";
        string hint = body;
        int bar = body.IndexOf('|');
        if (bar >= 0)
        {
            code = body.Substring(0, bar);
            hint = body.Substring(bar + 1);
        }

        if (code == "kOk")
        {
            problems.Clear();
            lastOkTime = Time.unscaledTime;
            errorText = "";
        }
        else if (code.Length > 0)
        {
            problems[code] = new Problem { hint = hint, time = Time.unscaledTime };
        }
        else if (body.StartsWith("Presage error", StringComparison.OrdinalIgnoreCase))
        {
            errorText = body;
        }
        else
        {
            infoText = body;
        }
    }

    // Turns a Presage validation code into a clear message for the player.
    static string Friendly(string code, string hint)
    {
        switch (code)
        {
            case "kChestNotVisible":
                return "Move back so your head, shoulders and chest are all in view";
            case "kFrameRateTooLow":
                return "Camera frame rate is too low: add more light or close other apps using the camera";
            case "kFaceNotCentered":
                return "You need to be more centered in the camera";
            case "kExcessiveMotion":
                return "Hold still: movement can throw off the reading";
        }

        // Unknown code: try the hint text, then fall back to Presage's own wording.
        string h = hint.ToLowerInvariant();
        if (h.Contains("chest")) return "Move back so your head, shoulders and chest are all in view";
        if (h.Contains("face")) return "I can't see your face: look toward the camera";
        if (h.Contains("light") || h.Contains("dark") || h.Contains("bright"))
            return "Lighting problem: face a light and avoid a bright window behind you";
        if (h.Contains("motion") || h.Contains("movement")) return "Hold still";
        return string.IsNullOrEmpty(hint) ? code : hint;
    }

    void HandleValue(float v)
    {
        lastValueTime = Time.unscaledTime;

        if (!seeded)
        {
            seeded = true;
            fast = slow = 0.5f;
            rollMin = rollMax = v;
        }

        // Rolling range that slowly follows the signal, so direction works on any scale.
        float decay = Mathf.Clamp01(Time.unscaledDeltaTime * 0.1f);
        rollMin = Mathf.Min(v, Mathf.Lerp(rollMin, v, decay));
        rollMax = Mathf.Max(v, Mathf.Lerp(rollMax, v, decay));
        float range = rollMax - rollMin;
        float norm = range > 1e-6f ? (v - rollMin) / range : 0.5f;

        fast = Mathf.Lerp(fast, norm, 0.3f);
        slow = Mathf.Lerp(slow, norm, 0.05f);
        float diff = fast - slow;
        if (diff > directionThreshold) directionSign = 1;
        else if (diff < -directionThreshold) directionSign = -1;

        if (starGlowScript != null)
        {
            starGlowScript.UpdateGlowFromBreathing(normalizeForGlow ? norm : v);
        }
    }

    // Returns the warnings Presage reported recently, newest first (max 3).
    string ActiveWarnings()
    {
        float now = Time.unscaledTime;
        var expired = new List<string>();
        var active = new List<KeyValuePair<string, Problem>>();
        foreach (var kv in problems)
        {
            if (now - kv.Value.time > problemHoldSeconds) expired.Add(kv.Key);
            else active.Add(kv);
        }
        foreach (string k in expired) problems.Remove(k);

        active.Sort((a, b) => b.Value.time.CompareTo(a.Value.time));
        var sb = new StringBuilder();
        for (int i = 0; i < active.Count && i < 3; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(Friendly(active[i].Key, active[i].Value.hint));
        }
        return sb.ToString();
    }

    void RefreshDisplay()
    {
        float now = Time.unscaledTime;
        string warnings = ActiveWarnings();

        if (bindError != null)
        {
            Show(bindError, Red, "");
        }
        else if (!anyPacket)
        {
            Show("Waiting for sender program (start breathing_udp_sender.exe)...", Orange, "");
        }
        else if (now - lastPacketTime > senderTimeoutSeconds)
        {
            Show("Lost connection to the sender program.", Orange, "");
        }
        else if (!string.IsNullOrEmpty(errorText))
        {
            Show(errorText, Red, "");
        }
        else if (now - lastValueTime < valueFreshSeconds)
        {
            // Data is flowing: always show in/out, with any warnings underneath.
            if (directionSign == 0)
            {
                Show("Breathing detected", Color.white, warnings);
            }
            else
            {
                bool breathingIn = (directionSign > 0) != invertDirection;
                Show(breathingIn ? "Breathing in" : "Breathing out",
                     breathingIn ? Color.white : Color.cyan, warnings);
            }
        }
        else if (warnings.Length > 0)
        {
            Show(warnings, Orange, "");
        }
        else if (now - lastOkTime < problemHoldSeconds)
        {
            Show("Hold still and keep breathing normally: waiting for breathing data", Color.white, "");
        }
        else
        {
            Show(string.IsNullOrEmpty(infoText) ? "Connected - waiting for breathing data..." : infoText,
                 Color.gray, "");
        }
    }

    void Show(string main, Color color, string warning)
    {
        mainMessage = main;
        mainColor = color;
        warningMessage = warning;

        if (messageText != null)
        {
            messageText.color = color;
            messageText.text = warning.Length > 0
                ? main + "\n<color=#FFA633>" + warning + "</color>"
                : main;
        }
    }

    void OnGUI()
    {
        if (messageText != null) return; // TMP text is handling display

        if (guiStyle == null)
        {
            guiStyle = new GUIStyle(GUI.skin.label)
            {
                wordWrap = true,
                alignment = TextAnchor.UpperLeft,
                fontStyle = FontStyle.Bold
            };
        }
        guiStyle.fontSize = guiFontSize;

        float width = Mathf.Min(Screen.width - 40, 1000);
        float mainHeight = guiStyle.CalcHeight(new GUIContent(mainMessage), width);
        var mainRect = new Rect(20, 20, width, mainHeight);
        DrawShadowed(mainRect, mainMessage, mainColor);

        if (warningMessage.Length > 0)
        {
            var warnRect = new Rect(20, 20 + mainHeight, width, Screen.height * 0.3f);
            DrawShadowed(warnRect, warningMessage, Orange);
        }
    }

    void DrawShadowed(Rect rect, string text, Color color)
    {
        guiStyle.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
        GUI.Label(new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height), text, guiStyle);
        guiStyle.normal.textColor = color;
        GUI.Label(rect, text, guiStyle);
    }

    void OnDestroy()
    {
        running = false;
        client?.Close();
    }
}