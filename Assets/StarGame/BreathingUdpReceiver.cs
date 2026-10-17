using System;
using System.Collections.Concurrent;
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
//   "0.123456"     a breathing value
//   "STATUS:text"  a status or validation message from Presage
//   "PING"         heartbeat, once per second
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

    [Header("Breathing direction")]
    [Tooltip("Turn on if the text says 'in' while you are breathing out.")]
    public bool invertDirection = false;
    [Tooltip("How big a change (as a fraction of the recent range) counts as in or out.")]
    public float directionThreshold = 0.03f;

    [Header("Glow input")]
    [Tooltip("Rescale values to 0-1 using a rolling min/max. Turn on when streaming a waveform field.")]
    public bool normalizeForGlow = false;

    private enum Kind { Info, Ok, Problem, Error }

    private readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
    private UdpClient client;
    private Thread thread;
    private volatile bool running;
    private string bindError;

    // Main-thread state
    private string statusText = "";
    private Kind statusKind = Kind.Info;
    private float lastStatusTime = -999f;
    private float lastValueTime = -999f;
    private float lastPacketTime = -999f;
    private bool anyPacket;

    // Direction tracking
    private bool seeded;
    private float fast, slow, rollMin, rollMax;
    private int directionSign; // +1 rising, -1 falling, 0 unknown

    private string displayMessage = "Waiting for sender program (breathing_udp_sender.exe)...";
    private Color displayColor = Color.gray;
    private GUIStyle guiStyle;

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
            statusText = msg.Substring(7).Trim();
            lastStatusTime = Time.unscaledTime;
            statusKind = Classify(statusText);
            return;
        }

        if (float.TryParse(msg, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
        {
            HandleValue(v);
        }
    }

    static Kind Classify(string text)
    {
        if (text.StartsWith("Tracking OK", StringComparison.OrdinalIgnoreCase)) return Kind.Ok;
        if (text.StartsWith("Presage error", StringComparison.OrdinalIgnoreCase)) return Kind.Error;
        if (text.StartsWith("Camera started", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Sender stopped", StringComparison.OrdinalIgnoreCase)) return Kind.Info;
        return Kind.Problem;
    }

    // Turns Presage's hint text into a clearer message for the player.
    static string Friendly(string hint)
    {
        string h = hint.ToLowerInvariant();
        if (h.Contains("chest")) return "Move back so your head, shoulders and chest are all in view";
        if (h.Contains("frames per second") || h.Contains("frame rate"))
            return "Camera frame rate is too low: add more light or close other apps using the camera";
        if (h.Contains("face")) return "I can't see your face: look toward the camera";
        if (h.Contains("center") || h.Contains("centre")) return "You need to be more centered in the camera";
        if (h.Contains("light") || h.Contains("dark") || h.Contains("bright"))
            return "Lighting problem: face a light and avoid a bright window behind you";
        if (h.Contains("movement") || h.Contains("motion")) return "Hold still";
        return hint;
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

    void RefreshDisplay()
    {
        float now = Time.unscaledTime;
        Color red = new Color(1f, 0.35f, 0.35f);
        Color orange = new Color(1f, 0.65f, 0.2f);

        if (bindError != null)
        {
            Show(bindError, red);
        }
        else if (!anyPacket)
        {
            Show("Waiting for sender program (start breathing_udp_sender.exe)...", orange);
        }
        else if (now - lastPacketTime > senderTimeoutSeconds)
        {
            Show("Lost connection to the sender program.", orange);
        }
        else if (statusKind == Kind.Error)
        {
            Show(statusText, red);
        }
        else if (statusKind == Kind.Problem && lastValueTime <= lastStatusTime)
        {
            Show(Friendly(statusText), orange);
        }
        else if (now - lastValueTime < valueFreshSeconds)
        {
            if (directionSign == 0)
            {
                Show("Breathing detected", Color.white);
            }
            else
            {
                bool breathingIn = (directionSign > 0) != invertDirection;
                Show(breathingIn ? "Breathing in" : "Breathing out",
                     breathingIn ? Color.white : Color.cyan);
            }
        }
        else if (statusKind == Kind.Ok)
        {
            Show("Tracking OK: hold still, waiting for breathing data", Color.white);
        }
        else
        {
            Show(string.IsNullOrEmpty(statusText)
                    ? "Connected - waiting for breathing data..."
                    : statusText, Color.gray);
        }
    }

    void Show(string text, Color color)
    {
        displayMessage = text;
        displayColor = color;
        if (messageText != null)
        {
            messageText.text = text;
            messageText.color = color;
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

        var rect = new Rect(20, 20, Mathf.Min(Screen.width - 40, 1000), Screen.height * 0.4f);

        // simple shadow so it is readable on any background
        guiStyle.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
        GUI.Label(new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height), displayMessage, guiStyle);
        guiStyle.normal.textColor = displayColor;
        GUI.Label(rect, displayMessage, guiStyle);
    }

    void OnDestroy()
    {
        running = false;
        client?.Close();
    }
}
