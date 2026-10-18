using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

// Star game ("Breathe with the star", inspired by Henrietta Leavitt's pulsing stars).
//
// The guide star (left) brightens and dims on a breathing schedule; the player's star (right)
// follows their real breathing, measured from the webcam by Presage SmartSpectra through
// breathing_udp_sender.exe. The player picks a difficulty, whether to show the breathing timer,
// and a game length; at the end they get a sync score and a few notes.
//
// Messages from the sender (port 5005):
//   TRACE:<ts_us>,<v>;<ts_us>,<v>;...  breathing waveform samples (chest movement)
//   RATE:<bpm>,<confidence>,<stable>   Presage breathing rate
//   STATUS:<code>|<name>|<hint>        validation status, 0 = OK (re-sent every second)
//   FACE:<cx>,<cy>,<w>,<h>             where the face is in the camera view (0-1)
//   INFO:<text> / ERROR:<text>
//   PING:<metrics>,<samples>,<frames>  heartbeat, once per second
// Camera preview (port 5006, debug only): "PV" + uint16 width + uint16 height + 2 pad + RGB bytes.
public class BreathingUdpReceiver : MonoBehaviour
{
    public enum Difficulty { Easy, Medium, Hard }

    [Header("References")]
    [Tooltip("The star the player's breathing controls (right).")]
    public StarBreathingGlow starGlowScript;
    [Tooltip("The computer's pulsing star (left). If empty, finds a GameObject named 'GuideStar'.")]
    public StarBreathingGlow guideStar;

    [Header("Network")]
    public int port = 5005;
    public int previewPort = 5006;
    public float senderTimeoutSeconds = 3f;

    [Header("Breathing sender (no terminal needed)")]
    [Tooltip("Start breathing_udp_sender.exe automatically (hidden) when the game starts, and stop it when the game ends. " +
             "Build it once with BreathingSender\\build_and_run.bat first.")]
    public bool autoStartSender = true;
    [Tooltip("The BreathingSender folder, relative to the project folder (Editor) or the game's folder (a build).")]
    public string senderFolder = "BreathingSender";

    [Header("Breathing signal")]
    [Tooltip("Flip if your star gets brighter when you breathe OUT (it should brighten on IN, like the guide star).")]
    public bool invertTrace = false;
    [Tooltip("Small buffer so the star moves smoothly between samples. Presage itself adds ~1-2 s of delay on top.")]
    public float playbackDelaySeconds = 0.3f;
    [Tooltip("Seconds of history used to scale the waveform to 0-1.")]
    public float normalizeWindowSeconds = 12f;

    [Header("Game")]
    [Tooltip("Breath-in / breath-out lengths (whole seconds, min-max) for each difficulty.")]
    public Vector2Int easySeconds = new Vector2Int(4, 6);
    public Vector2Int mediumSeconds = new Vector2Int(3, 7);
    public Vector2Int hardSeconds = new Vector2Int(2, 8);
    [Tooltip("Game lengths the player can pick from (seconds).")]
    public float[] lengthChoices = { 20f, 30f, 60f };
    public float getReadySeconds = 3f;
    [Tooltip("Fired when a game finishes (e.g. to light this star in the constellation).")]
    public UnityEvent onStarLit;

    [Header("Online coach (Gemini) + Tiger Data")]
    [Tooltip("After each game, send it to the server: it's saved in Tiger Data and Gemini writes a tip. " +
             "Without the server the game still works and shows its built-in notes.")]
    public bool useServer = true;
    public string sessionUrl = "https://149-28-227-46.sslip.io/api/shockwave/star/session";
    public float serverTimeoutSeconds = 12f;

    [Header("Warnings")]
    [Tooltip("A position warning must last this long before it is shown (stops flicker).")]
    public float warningPersistSeconds = 1.5f;

    [Header("Fonts (auto-filled from Assets/Fonts)")]
    [Tooltip("Titles, banner and the breathing coach")]
    public Font titleFont;
    [Tooltip("All regular text and buttons")]
    public Font bodyFont;

    [Header("Display")]
    [Tooltip("Debug panel with the live breathing graph (toggle with the Debug button).")]
    public bool showDebugInfo = false;
    [Tooltip("The full camera picture (toggle with the Video button). The head-position box is always shown.")]
    public bool showVideoPreview = false;
    public int guiFontSize = 26;

    // ---------- threading ----------
    private readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
    private UdpClient client, previewClient;
    private Thread thread, previewThread;
    private volatile bool running;
    private string bindError;
    private readonly object previewLock = new object();
    private byte[] pendingPreview;

    // ---------- connection state ----------
    private bool anyPacket;
    private float lastPacketTime = -999f, lastTraceTime = -999f, lastPreviewTime = -999f;
    private long pingMetrics = -1, pingSamples = -1, pingFrames = -1;
    private string infoText = "", errorText = "";

    // ---------- Presage status ----------
    private int statusCode = -1;
    private string statusName = "", statusHint = "";
    private float statusSince;

    // ---------- face position (for the box-and-dot guide) ----------
    private float faceX, faceY, faceW, faceH, lastFaceTime = -999f;

    // ---------- rate (debug only) ----------
    private float presageRate = -1f, presageConf = -1f, lastRateTime = -999f;
    private bool presageStable;
    private float estimatedRate = -1f;

    // ---------- waveform ----------
    private struct Sample { public double t; public float v; }
    private readonly List<Sample> samples = new List<Sample>();
    private long firstTs = long.MinValue, lastTs = long.MinValue;
    private double playhead;
    private bool playheadInit;
    private float currentNorm = 0.5f;   // 0..1 of the user's breath right now
    private int direction;              // +1 in, -1 out

    // ---------- game ----------
    private enum Phase { Menu, GetReady, Playing, Results }
    private Phase phase = Phase.Menu;
    private Difficulty difficulty = Difficulty.Easy;
    private bool timerOn = true;
    private int lengthIndex = 1;                 // 30 s by default
    private float SessionLength => lengthChoices != null && lengthChoices.Length > 0
        ? lengthChoices[Mathf.Clamp(lengthIndex, 0, lengthChoices.Length - 1)] : 30f;
    private float phaseStart;

    private struct Breath { public float inhale, exhale; }
    private readonly List<Breath> schedule = new List<Breath>();
    private System.Random rng = new System.Random();
    private float easyRhythm = 4f;              // Easy: the one rhythm for the whole game
    private float guideTime;                    // seconds since the game's first breath
    private float guideLevel;                   // 0 = dim (breathed out) .. 1 = bright (breathed in)
    private bool guideInhale = true;
    private float guideSecondsLeft;             // seconds left in the current in / out
    private float idleTime;                     // gentle pulse on the menu and results screens

    // Recorded each frame while playing, for the score.
    private readonly List<float> recT = new List<float>(), recUser = new List<float>();
    private float lostSeconds;

    private struct Result
    {
        public int score, breaths;
        public float inhaleMatch, exhaleMatch, tracked, lag;
        public bool enoughData;
        public List<string> notes;
    }
    private Result result;

    // ---------- server (Tiger Data + Gemini) ----------
    [Serializable] private class SessionPayload
    {
        public string player, difficulty;
        public bool timerOn;
        public float length, inhaleMatch, exhaleMatch, tracked, lag;
        public int score;
        public float[] t, u, g;      // seconds, player's breathing (-1 = not seen), guide at the player's delay
        public bool[] inhale;
    }
    [Serializable] private class Bucket { public float start, match, seen; }
    [Serializable] private class HistoryItem { public int score; public string difficulty; }
    [Serializable] private class SessionReply
    {
        public string coach, source, error;
        public bool stored;
        public int sessionId, yourBest, bestToday, bestForDifficulty;
        public Bucket[] buckets;
        public HistoryItem[] history;
    }
    private enum ServerState { Off, Waiting, Done, Failed }
    private ServerState serverState = ServerState.Off;
    private SessionReply reply;

    // ---------- auto-started sender ----------
    private System.Diagnostics.Process senderProcess;
    private string senderNote = "";
    private readonly Queue<string> senderLog = new Queue<string>();

    // ---------- preview ----------
    private Texture2D previewTex;
    private byte[] flipBuffer;
    private float frameAspect = 16f / 9f;

    // ---------- GUI ----------
    private GUIStyle style, smallStyle, buttonStyle, toggleStyle;
    private Texture2D white;
    static readonly Color Red = new Color(1f, 0.35f, 0.35f);
    static readonly Color Orange = new Color(1f, 0.65f, 0.2f);
    static readonly Color Green = new Color(0.4f, 1f, 0.5f);
    static readonly Color Gold = new Color(1f, 0.85f, 0.4f);
    static readonly Color PanelColor = new Color(0.03f, 0.04f, 0.06f, 0.92f);

    // ======================================================================
    void Awake()
    {
        GameFonts.Title = titleFont;
        GameFonts.Body = bodyFont;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (!titleFont) titleFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(GameFonts.TitlePath);
        if (!bodyFont || GameFonts.IsOldFont(bodyFont)) bodyFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(GameFonts.BodyPath);
        style = null; // rebuild styles if fonts change
    }
#endif

    void Start()
    {
        if (guideStar == null)
        {
            var go = GameObject.Find("GuideStar");
            if (go != null) guideStar = go.GetComponent<StarBreathingGlow>();
        }
        if (starGlowScript != null)
        {
            starGlowScript.useAutoPulseTest = false;
            starGlowScript.smoothingSpeed = Mathf.Max(starGlowScript.smoothingSpeed, 5f);
        }
        if (guideStar != null)
        {
            guideStar.useAutoPulseTest = false;   // this script drives the guide's schedule
            guideStar.smoothingSpeed = 20f;       // follow the schedule closely
        }

        try
        {
            client = new UdpClient(port);
            client.Client.ReceiveBufferSize = 1 << 20;
            running = true;
            thread = new Thread(Listen) { IsBackground = true };
            thread.Start();
        }
        catch (Exception e)
        {
            bindError = "Could not open UDP port " + port + " (another program or a second receiver is using it).";
            Debug.LogError("BreathingUdpReceiver: " + e.Message);
        }

        try
        {
            previewClient = new UdpClient(previewPort);
            previewClient.Client.ReceiveBufferSize = 1 << 21;
            running = true;
            previewThread = new Thread(ListenPreview) { IsBackground = true };
            previewThread.Start();
        }
        catch (Exception e)
        {
            Debug.LogWarning("BreathingUdpReceiver: preview port " + previewPort + " unavailable: " + e.Message);
        }

        if (autoStartSender) StartSender();
    }

    void Listen()
    {
        var ep = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try { inbox.Enqueue(Encoding.UTF8.GetString(client.Receive(ref ep)).Trim()); }
            catch (SocketException) { if (!running) break; Thread.Sleep(20); }
            catch (ObjectDisposedException) { break; }
        }
    }

    void ListenPreview()
    {
        var ep = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                byte[] d = previewClient.Receive(ref ep);
                if (d.Length > 8 && d[0] == 'P' && d[1] == 'V')
                    lock (previewLock) pendingPreview = d;
            }
            catch (SocketException) { if (!running) break; Thread.Sleep(20); }
            catch (ObjectDisposedException) { break; }
        }
    }

    // ======================================================================
    void Update()
    {
        while (inbox.TryDequeue(out string msg)) Handle(msg);
        UpdatePreviewTexture();
        UpdatePlayback();
        UpdateGame(Time.unscaledDeltaTime);
        if (guideStar != null) guideStar.UpdateGlowFromBreathing(guideLevel);
    }

    void Handle(string msg)
    {
        lastPacketTime = Time.unscaledTime;
        anyPacket = true;

        if (msg.StartsWith("TRACE:", StringComparison.Ordinal)) HandleTrace(msg.Substring(6));
        else if (msg.StartsWith("FACE:", StringComparison.Ordinal)) HandleFace(msg.Substring(5));
        else if (msg.StartsWith("RATE:", StringComparison.Ordinal)) HandleRate(msg.Substring(5));
        else if (msg.StartsWith("STATUS:", StringComparison.Ordinal)) HandleStatus(msg.Substring(7));
        else if (msg.StartsWith("PING", StringComparison.Ordinal)) HandlePing(msg);
        else if (msg.StartsWith("ERROR:", StringComparison.Ordinal)) errorText = msg.Substring(6);
        else if (msg.StartsWith("INFO:", StringComparison.Ordinal))
        {
            infoText = msg.Substring(5);
            if (infoText.StartsWith("Camera started")) errorText = "";
        }
        else if (float.TryParse(msg, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
        {
            // Old sender format (single number). Treat it as a trace sample.
            AddSample((long)(Time.unscaledTimeAsDouble * 1e6), v);
        }
    }

    void HandleFace(string body)
    {
        string[] p = body.Split(',');
        if (p.Length < 4) return;
        if (float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
            float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float w) &&
            float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float h))
        {
            faceX = x; faceY = y; faceW = w; faceH = h;
            lastFaceTime = Time.unscaledTime;
        }
    }

    void HandlePing(string msg)
    {
        int c = msg.IndexOf(':');
        if (c < 0) return;
        string[] p = msg.Substring(c + 1).Split(',');
        if (p.Length >= 3)
        {
            long.TryParse(p[0], out pingMetrics);
            long.TryParse(p[1], out pingSamples);
            long.TryParse(p[2], out pingFrames);
        }
    }

    void HandleStatus(string body)
    {
        string[] p = body.Split(new[] { '|' }, 3);
        if (p.Length >= 1 && int.TryParse(p[0], out int code))
        {
            if (code != statusCode) statusSince = Time.unscaledTime;
            statusCode = code;
            statusName = p.Length > 1 ? p[1] : "";
            statusHint = p.Length > 2 ? p[2] : "";
        }
        else
        {
            statusCode = -1; statusName = ""; statusHint = body; // old sender format
        }
    }

    void HandleRate(string body)
    {
        string[] p = body.Split(',');
        if (p.Length < 1 || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)) return;
        presageRate = r;
        presageConf = p.Length > 1 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float c) ? c : -1f;
        presageStable = p.Length > 2 && p[2] == "1";
        lastRateTime = Time.unscaledTime;
    }

    void HandleTrace(string body)
    {
        foreach (string pair in body.Split(';'))
        {
            int comma = pair.IndexOf(',');
            if (comma < 0) continue;
            if (!long.TryParse(pair.Substring(0, comma), out long ts)) continue;
            if (!float.TryParse(pair.Substring(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) continue;
            if (ts == 0) ts = (long)(Time.unscaledTimeAsDouble * 1e6);
            AddSample(ts, v);
        }
    }

    void AddSample(long ts, float v)
    {
        if (lastTs != long.MinValue && ts < lastTs - 5_000_000)
        {
            // Sender restarted: start over.
            samples.Clear(); firstTs = long.MinValue; playheadInit = false;
        }
        if (lastTs != long.MinValue && ts <= lastTs) return;
        if (firstTs == long.MinValue) firstTs = ts;
        lastTs = ts;
        samples.Add(new Sample { t = (ts - firstTs) / 1e6, v = invertTrace ? -v : v });
        lastTraceTime = Time.unscaledTime;

        double cutoff = samples[samples.Count - 1].t - 60.0;
        int drop = 0;
        while (drop < samples.Count && samples[drop].t < cutoff) drop++;
        if (drop > 0) samples.RemoveRange(0, drop);
    }

    // Plays the waveform back slightly delayed so the star moves smoothly between samples.
    void UpdatePlayback()
    {
        if (samples.Count < 2) return;
        double latest = samples[samples.Count - 1].t;
        if (!playheadInit) { playhead = latest - playbackDelaySeconds; playheadInit = true; }
        else playhead += Time.unscaledDeltaTime;

        if (playhead > latest) playhead = latest;                                   // waiting for data
        if (playhead < latest - playbackDelaySeconds * 3) playhead = latest - playbackDelaySeconds; // fell behind

        float v = ValueAt(playhead);
        Percentiles(playhead - normalizeWindowSeconds, playhead, 0.1f, 0.9f, out float lo, out float hi);
        float range = hi - lo;
        float norm = range > 1e-6f ? Mathf.Clamp01((v - lo) / range) : 0.5f;

        float prev = currentNorm;
        currentNorm = norm;
        float d = currentNorm - prev;
        if (d > 0.004f) direction = 1; else if (d < -0.004f) direction = -1;

        // The glow script is brightest at 1 (breathed in) and dimmest at 0, same as the guide star.
        if (starGlowScript != null) starGlowScript.UpdateGlowFromBreathing(norm);

        estimatedRate = EstimateRate(30.0);
    }

    float ValueAt(double t)
    {
        int lo = 0, hi = samples.Count - 1;
        if (t <= samples[0].t) return samples[0].v;
        if (t >= samples[hi].t) return samples[hi].v;
        while (hi - lo > 1) { int mid = (lo + hi) / 2; if (samples[mid].t <= t) lo = mid; else hi = mid; }
        double span = samples[hi].t - samples[lo].t;
        float f = span > 1e-9 ? (float)((t - samples[lo].t) / span) : 0f;
        return Mathf.Lerp(samples[lo].v, samples[hi].v, f);
    }

    void MinMax(double from, double to, out float lo, out float hi)
    {
        lo = float.MaxValue; hi = float.MinValue;
        foreach (var s in samples)
        {
            if (s.t < from || s.t > to) continue;
            if (s.v < lo) lo = s.v;
            if (s.v > hi) hi = s.v;
        }
        if (lo > hi) { lo = 0; hi = 0; }
    }

    // Typical low and high of the waveform (ignoring the outer 10% each side), so one deep breath or a
    // noise spike doesn't stop ordinary breaths from reaching full brightness.
    readonly List<float> pctBuf = new List<float>();
    void Percentiles(double from, double to, float pLo, float pHi, out float lo, out float hi)
    {
        pctBuf.Clear();
        foreach (var s in samples) if (s.t >= from && s.t <= to) pctBuf.Add(s.v);
        if (pctBuf.Count < 5) { MinMax(from, to, out lo, out hi); return; }
        pctBuf.Sort();
        lo = pctBuf[Mathf.Clamp(Mathf.RoundToInt(pLo * (pctBuf.Count - 1)), 0, pctBuf.Count - 1)];
        hi = pctBuf[Mathf.Clamp(Mathf.RoundToInt(pHi * (pctBuf.Count - 1)), 0, pctBuf.Count - 1)];
    }

    // Breaths/min from the waveform (debug panel only).
    float EstimateRate(double windowSeconds)
    {
        if (samples.Count < 10) return -1f;
        double end = samples[samples.Count - 1].t, start = end - windowSeconds;
        MinMax(start, end, out float lo, out float hi);
        float range = hi - lo;
        if (range < 1e-6f) return -1f;

        var crossings = new List<double>();
        bool armed = false;
        foreach (var s in samples)
        {
            if (s.t < start) continue;
            float n = (s.v - lo) / range;
            if (n < 0.35f) armed = true;
            else if (n > 0.65f && armed) { crossings.Add(s.t); armed = false; }
        }
        if (crossings.Count < 3) return -1f;
        double span = crossings[crossings.Count - 1] - crossings[0];
        return span > 1 ? (float)(60.0 * (crossings.Count - 1) / span) : -1f;
    }

    bool DataFlowing => anyPacket && Time.unscaledTime - lastTraceTime < senderTimeoutSeconds;
    bool FaceFresh => Time.unscaledTime - lastFaceTime < 1.5f;
    // 10 camera tuning, 11 frame rate, 12 motion: Presage still measures, so these are only hints.
    static bool IsSoftCode(int code) => code == 10 || code == 11 || code == 12;
    bool StatusPersisted => Time.unscaledTime - statusSince >= warningPersistSeconds;
    bool PositionProblem => statusCode > 0 && !IsSoftCode(statusCode) && StatusPersisted;
    bool SoftWarning => statusCode > 0 && IsSoftCode(statusCode) && StatusPersisted;
    bool Tracking => DataFlowing && !PositionProblem;

    // ======================================================================
    // Guide schedule

    Vector2Int Range => difficulty == Difficulty.Easy ? easySeconds : difficulty == Difficulty.Medium ? mediumSeconds : hardSeconds;
    int RandomSeconds() { var r = Range; return rng.Next(Mathf.Max(1, r.x), Mathf.Max(r.x, r.y) + 1); }

    // Easy: one rhythm all game. Medium: new length every breath (in = out). Hard: in and out change separately.
    Breath MakeBreath()
    {
        switch (difficulty)
        {
            case Difficulty.Easy: return new Breath { inhale = easyRhythm, exhale = easyRhythm };
            case Difficulty.Medium: { float h = RandomSeconds(); return new Breath { inhale = h, exhale = h }; }
            default: return new Breath { inhale = RandomSeconds(), exhale = RandomSeconds() };
        }
    }

    // The guide's brightness at time t of the game (0 = dim, 1 = bright), from the schedule.
    float GuideAt(float t, out bool inhale, out float secondsLeft)
    {
        inhale = true; secondsLeft = 0f;
        if (t < 0f) return 0f;
        float start = 0f;
        for (int i = 0; ; i++)
        {
            while (schedule.Count <= i) schedule.Add(MakeBreath());
            Breath b = schedule[i];
            float into = t - start;
            if (into < b.inhale)
            {
                inhale = true; secondsLeft = b.inhale - into;
                return Ease(into / b.inhale);
            }
            if (into < b.inhale + b.exhale)
            {
                inhale = false; secondsLeft = b.inhale + b.exhale - into;
                return 1f - Ease((into - b.inhale) / b.exhale);
            }
            start += b.inhale + b.exhale;
        }
    }

    static float Ease(float p) { p = Mathf.Clamp01(p); return p * p * (3f - 2f * p); }

    int BreathsIn(float length)
    {
        float start = 0f; int n = 0;
        for (int i = 0; start < length; i++)
        {
            while (schedule.Count <= i) schedule.Add(MakeBreath());
            start += schedule[i].inhale + schedule[i].exhale;
            n++;
        }
        return n;
    }

    // ======================================================================
    // Game flow

    void StartGame()
    {
        rng = new System.Random();
        schedule.Clear();
        easyRhythm = RandomSeconds();
        recT.Clear(); recUser.Clear();
        lostSeconds = 0f;
        guideTime = 0f;
        liveSync = -1f; nextLiveSync = 0f; nextLiveLag = 0f;
        phase = Phase.GetReady;
        phaseStart = Time.unscaledTime;
    }

    void UpdateGame(float dt)
    {
        switch (phase)
        {
            case Phase.Menu:
            case Phase.Results:
                // A calm 4-in / 4-out pulse so the guide star is alive while choosing.
                idleTime += dt;
                guideLevel = 0.5f - 0.5f * Mathf.Cos(idleTime * Mathf.PI / 4f);
                guideInhale = Mathf.Sin(idleTime * Mathf.PI / 4f) > 0f;
                break;

            case Phase.GetReady:
                guideLevel = Mathf.MoveTowards(guideLevel, 0f, dt * 2f);   // settle dim, ready to breathe in
                guideInhale = true;
                if (Time.unscaledTime - phaseStart >= getReadySeconds)
                {
                    phase = Phase.Playing;
                    phaseStart = Time.unscaledTime;
                    guideTime = 0f;
                }
                break;

            case Phase.Playing:
                guideTime += dt;
                guideLevel = GuideAt(guideTime, out guideInhale, out guideSecondsLeft);

                recT.Add(guideTime);
                recUser.Add(Tracking ? currentNorm : float.NaN);
                if (!Tracking) lostSeconds += dt;
                UpdateLiveSync();

                if (guideTime >= SessionLength)
                {
                    result = ComputeResult();
                    phase = Phase.Results;
                    reply = null;
                    serverState = ServerState.Off;
                    if (useServer && result.enoughData && !string.IsNullOrEmpty(sessionUrl)) StartCoroutine(SendSession());
                    idleTime = 0f;
                    onStarLit?.Invoke();
                }
                break;
        }
    }

    // ======================================================================
    // Scoring: how closely the player's breathing followed the guide.
    // Presage adds ~1-2 s of delay and people react a little late, so the guide is compared at the
    // best delay between 0 and 4 s. Two parts, both forgiving:
    //   70%  shape: correlation of the two curves (0.3 or less = 0, 0.9 or more = full marks)
    //   30%  direction: breathing in while the star brightens, out while it dims (55% = 0, 90% = full)
    // Tested on simulated players: following the star ~95-100, loosely following ~70-100,
    // ignoring it ~0-40, random ~0.
    float Combined(float corr, float dir) =>
        0.7f * Mathf.Clamp01((corr - 0.3f) / 0.6f) + 0.3f * Mathf.Clamp01((dir - 0.55f) / 0.35f);

    Result ComputeResult()
    {
        var r = new Result { notes = new List<string>() };
        float length = SessionLength;
        r.breaths = BreathsIn(length);
        r.tracked = Mathf.Clamp01(1f - lostSeconds / Mathf.Max(1f, length));

        int valid = 0;
        for (int i = 0; i < recT.Count; i++) if (!float.IsNaN(recUser[i])) valid++;
        r.enoughData = valid >= 30 && r.tracked > 0.25f;
        if (!r.enoughData)
        {
            r.notes.Add("We couldn't see your breathing for most of the game. Check the head-position box: " +
                        "sit back so your head and chest are in view, then try again.");
            return r;
        }

        float lag = BestLag(0f, 4f, 0.1f, 0f, out float corr);
        Direction(lag, 0f, out float all, out r.inhaleMatch, out r.exhaleMatch);
        r.lag = lag;
        r.score = Mathf.RoundToInt(100f * Combined(corr, all));

        // Notes
        if (r.score >= 80) r.notes.Add("Great rhythm: your breathing rose and fell right along with the star.");
        else if (r.score >= 60) r.notes.Add("Nicely done: you followed the star for most of the game.");
        else if (r.score >= 40) r.notes.Add("You caught the rhythm in places. Watch the guide star and let each breath stretch to match it.");
        else r.notes.Add("The star and your breathing mostly went separate ways. Try slower, deeper breaths that follow the glow.");

        if (r.inhaleMatch < r.exhaleMatch - 0.15f)
            r.notes.Add("Your breaths in were the trickiest: start breathing in as soon as the star starts to brighten.");
        else if (r.exhaleMatch < r.inhaleMatch - 0.15f)
            r.notes.Add(difficulty == Difficulty.Hard
                ? "Your breaths out were the trickiest: on Hard some are long, so let the air out slowly until the star is fully dim."
                : "Your breaths out were the trickiest: keep breathing out slowly until the star is fully dim.");

        if (r.tracked < 0.85f)
            r.notes.Add($"We lost sight of you for about {Mathf.RoundToInt(lostSeconds)} s. Keep your head in the box and hold still.");

        if (r.score >= 75)
        {
            if (difficulty != Difficulty.Hard) r.notes.Add($"Ready for more? Try {(difficulty == Difficulty.Easy ? "Medium" : "Hard")}.");
            else if (timerOn) r.notes.Add("Try Hard with the timer off for the full challenge.");
        }
        return r;
    }

    // The delay (seconds) at which the player's curve best matches the guide, using recordings from `fromT` on.
    float BestLag(float minLag, float maxLag, float step, float fromT, out float bestCorr)
    {
        bestCorr = -1f;
        float best = 0f;
        for (float lag = minLag; lag <= maxLag + 0.001f; lag += step)
        {
            float c = Correlation(lag, fromT);
            if (c > bestCorr) { bestCorr = c; best = lag; }
        }
        return best;
    }

    float Correlation(float lag, float fromT)
    {
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0; int m = 0;
        for (int i = 0; i < recT.Count; i++)
        {
            float u = recUser[i];
            if (float.IsNaN(u) || recT[i] < fromT || recT[i] - lag < 0f) continue;
            float g = GuideAt(recT[i] - lag, out _, out _);
            sx += g; sy += u; sxx += g * g; syy += u * u; sxy += g * u; m++;
        }
        if (m < 20) return -1f;
        double cov = sxy / m - (sx / m) * (sy / m);
        double vx = sxx / m - (sx / m) * (sx / m), vy = syy / m - (sy / m) * (sy / m);
        if (vx < 1e-6 || vy < 1e-6) return 0f;
        return (float)(cov / Math.Sqrt(vx * vy));
    }

    // Share of the time the player breathed the same way as the guide (in while it brightens, out while it dims),
    // compared over 0.8 s steps and skipping the turning points. Also split into breath-in and breath-out parts.
    void Direction(float lag, float fromT, out float all, out float inhale, out float exhale)
    {
        int inAgree = 0, inTotal = 0, outAgree = 0, outTotal = 0, p = 0;
        for (int i = 0; i < recT.Count; i++)
        {
            if (float.IsNaN(recUser[i]) || recT[i] < fromT || recT[i] - lag < 0f) continue;
            while (p < i && recT[p] < recT[i] - 0.8f) p++;
            if (p >= i || float.IsNaN(recUser[p])) continue;
            float g1 = GuideAt(recT[i] - lag, out bool inh, out _), g0 = GuideAt(recT[p] - lag, out _, out _);
            float dg = g1 - g0, du = recUser[i] - recUser[p];
            if (Mathf.Abs(dg) < 0.08f) continue;
            bool agree = (du > 0f) == (dg > 0f);
            if (inh) { inTotal++; if (agree) inAgree++; } else { outTotal++; if (agree) outAgree++; }
        }
        inhale = inTotal > 0 ? (float)inAgree / inTotal : 0f;
        exhale = outTotal > 0 ? (float)outAgree / outTotal : 0f;
        all = inTotal + outTotal > 0 ? (float)(inAgree + outAgree) / (inTotal + outTotal) : 0f;
    }

    // Live "in sync" meter while playing: the same score over the last 10 s, at a delay re-estimated every 3 s.
    private float liveSync = -1f, liveLag = 1.5f, nextLiveSync, nextLiveLag;
    void UpdateLiveSync()
    {
        if (guideTime < 6f) { liveSync = -1f; return; }
        if (guideTime >= nextLiveLag)
        {
            nextLiveLag = guideTime + 3f;
            liveLag = BestLag(0f, 4f, 0.2f, Mathf.Max(0f, guideTime - 20f), out _);
        }
        if (guideTime >= nextLiveSync)
        {
            nextLiveSync = guideTime + 0.5f;
            float from = Mathf.Max(0f, guideTime - 10f);
            float c = Correlation(liveLag, from);
            Direction(liveLag, from, out float d, out _, out _);
            float target = c < -0.5f ? -1f : Combined(c, d);
            liveSync = liveSync < 0f || target < 0f ? target : Mathf.Lerp(liveSync, target, 0.5f);
        }
    }

    // ======================================================================
    // Server: send the finished game (10 samples a second) to be stored in Tiger Data and coached by Gemini.
    System.Collections.IEnumerator SendSession()
    {
        serverState = ServerState.Waiting;
        var tl = new List<float>(); var ul = new List<float>(); var gl = new List<float>(); var il = new List<bool>();
        float next = 0f;
        for (int i = 0; i < recT.Count; i++)
        {
            if (recT[i] < next) continue;
            next = recT[i] + 0.1f;
            float g = GuideAt(recT[i] - result.lag, out bool inh, out _);
            tl.Add(recT[i]); ul.Add(float.IsNaN(recUser[i]) ? -1f : recUser[i]); gl.Add(g); il.Add(inh);
        }
        var payload = new SessionPayload
        {
            player = PlayerId(), difficulty = difficulty.ToString(), timerOn = timerOn, length = SessionLength,
            score = result.score, inhaleMatch = result.inhaleMatch, exhaleMatch = result.exhaleMatch,
            tracked = result.tracked, lag = result.lag,
            t = tl.ToArray(), u = ul.ToArray(), g = gl.ToArray(), inhale = il.ToArray(),
        };

        using (var www = new UnityWebRequest(sessionUrl, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload)));
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.timeout = Mathf.CeilToInt(serverTimeoutSeconds);
            UnityWebRequestAsyncOperation op = null;
            try { op = www.SendWebRequest(); }
            catch (Exception e) { Debug.LogWarning("[Star] " + e.Message); }
            if (op != null) yield return op;

            if (op != null && www.result == UnityWebRequest.Result.Success)
            {
                reply = JsonUtility.FromJson<SessionReply>(www.downloadHandler.text);
                if (reply != null && !string.IsNullOrEmpty(reply.error)) Debug.LogWarning("[Star] Server: " + reply.error);
                serverState = reply != null ? ServerState.Done : ServerState.Failed;
            }
            else
            {
                if (op != null) Debug.LogWarning("[Star] " + www.error + " (is the server running?)");
                serverState = ServerState.Failed;
            }
        }
    }

    // A short, stable id for this computer, so the server can show "your" earlier games.
    static string PlayerId()
    {
        string id = SystemInfo.deviceUniqueIdentifier ?? "guest";
        uint h = 2166136261;
        foreach (char c in id) { h ^= c; h *= 16777619; }
        return "pc-" + h.ToString("x8");
    }

    // ======================================================================
    // Auto-start the sender (Windows): runs BreathingSender\build\breathing_udp_sender.exe hidden,
    // with the SDK path and API key from BreathingSender\local_config.bat. Stopped when the game ends.
    void StartSender()
    {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        try
        {
            if (System.Diagnostics.Process.GetProcessesByName("breathing_udp_sender").Length > 0)
            {
                senderNote = "Using the breathing sender that's already running.";
                return;
            }
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", senderFolder));
            string exe = Path.Combine(folder, "build", "breathing_udp_sender.exe");
            string config = Path.Combine(folder, "local_config.bat");
            if (!File.Exists(exe)) { senderNote = "Breathing sender not built yet: run BreathingSender\\build_and_run.bat once."; return; }
            if (!File.Exists(config)) { senderNote = "No BreathingSender\\local_config.bat yet: run build_and_run.bat once to set it up."; return; }

            var vars = ReadConfig(config);
            vars.TryGetValue("SMARTSPECTRA_SDK_PATH", out string sdk);
            vars.TryGetValue("SMARTSPECTRA_API_KEY", out string key);
            if (string.IsNullOrEmpty(key)) { senderNote = "No API key in BreathingSender\\local_config.bat."; return; }

            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                WorkingDirectory = folder,            // camera.txt lives here
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,         // closed straight away: camera choice falls back to saved / default
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.EnvironmentVariables["SMARTSPECTRA_API_KEY"] = key;
            if (!string.IsNullOrEmpty(sdk))
            {
                psi.EnvironmentVariables["SMARTSPECTRA_SDK_PATH"] = sdk;
                psi.EnvironmentVariables["PATH"] = Path.Combine(sdk, "bin") + ";" + Environment.GetEnvironmentVariable("PATH");
            }

            senderProcess = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
            senderProcess.OutputDataReceived += (_, e) => LogSender(e.Data);
            senderProcess.ErrorDataReceived += (_, e) => LogSender(e.Data);
            senderProcess.Start();
            senderProcess.StandardInput.Close();
            senderProcess.BeginOutputReadLine();
            senderProcess.BeginErrorReadLine();
            senderNote = "Starting the camera (this takes a few seconds)...";
        }
        catch (Exception e)
        {
            senderNote = "Couldn't start the breathing sender: " + e.Message;
            Debug.LogWarning("BreathingUdpReceiver: " + e);
        }
#else
        senderNote = "Run BreathingSender\\build_and_run.bat (auto-start only works on Windows).";
#endif
    }

    // Reads `set "NAME=value"` lines from local_config.bat.
    static Dictionary<string, string> ReadConfig(string path)
    {
        var d = new Dictionary<string, string>();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (!line.StartsWith("set ", StringComparison.OrdinalIgnoreCase)) continue;
            line = line.Substring(4).Trim().Trim('"');
            int eq = line.IndexOf('=');
            if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        return d;
    }

    void LogSender(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (senderLog)
        {
            senderLog.Enqueue(line);
            while (senderLog.Count > 6) senderLog.Dequeue();
        }
    }

    void StopSender()
    {
        try
        {
            if (senderProcess != null && !senderProcess.HasExited) senderProcess.Kill();
        }
        catch (Exception) { }
        senderProcess = null;
    }

    void UpdatePreviewTexture()
    {
        byte[] d;
        lock (previewLock) { d = pendingPreview; pendingPreview = null; }
        if (d == null) return;
        int w = d[2] | (d[3] << 8), h = d[4] | (d[5] << 8);
        if (w <= 0 || h <= 0 || d.Length < 8 + w * h * 3) return;
        frameAspect = (float)w / h;
        lastPreviewTime = Time.unscaledTime;
        if (!showVideoPreview) return;   // the picture itself is only decoded when it's shown
        if (previewTex == null || previewTex.width != w || previewTex.height != h)
        {
            previewTex = new Texture2D(w, h, TextureFormat.RGB24, false) { filterMode = FilterMode.Bilinear };
            flipBuffer = new byte[w * h * 3];
        }
        int row = w * 3;
        for (int y = 0; y < h; y++) Buffer.BlockCopy(d, 8 + y * row, flipBuffer, (h - 1 - y) * row, row); // Unity textures are bottom-up
        previewTex.LoadRawTextureData(flipBuffer);
        previewTex.Apply(false);
    }

    // ======================================================================
    // Player-facing messages
    static string PositionMessage(int code, string hint)
    {
        switch (code)
        {
            case 1: return "I can't see your face - look at the camera";
            case 2: return "Only one person in front of the camera, please";
            case 3: return "Move to the center of the camera view";
            case 4: return "Adjust your distance from the camera";
            case 5: return "Too dark - turn on a light in front of you";
            case 6: return "Too bright - avoid a bright window or light behind you";
            case 7: return "Sit back so your shoulders and chest are in view";
            case 10: return "Camera is adjusting - hold still";
            case 11: return "Camera is too slow - add light or close other apps using the camera";
            case 12: return "Hold still - too much movement";
            case 13: return "Move back - you're too close";
            case 14: return "Move closer to the camera";
            case 15: return "Move down a little - your face is too high in the frame";
            case 16: return "Move up a little - your face is too low in the frame";
            case 17: return "Face the camera straight on";
        }
        return string.IsNullOrEmpty(hint) ? "Adjust your position" : hint;
    }

    // Why the game can't read breathing right now (null = it can).
    string ConnectionProblem()
    {
        float now = Time.unscaledTime;
        if (bindError != null) return bindError;
        if (!anyPacket)
            return string.IsNullOrEmpty(senderNote) ? "Waiting for the breathing sender - run BreathingSender\\build_and_run.bat" : senderNote;
        if (now - lastPacketTime > senderTimeoutSeconds) return "Lost connection to the breathing sender";
        if (!string.IsNullOrEmpty(errorText)) return "Presage error: " + errorText;
        if (PositionProblem) return PositionMessage(statusCode, statusHint);
        if (!DataFlowing) return "Hold still and breathe normally - starting to read your breathing...";
        return null;
    }

    // ======================================================================
    void OnGUI()
    {
        EnsureStyles();
        style.fontSize = guiFontSize;
        smallStyle.fontSize = Mathf.Max(13, Mathf.RoundToInt(guiFontSize * 0.62f));

        switch (phase)
        {
            case Phase.Menu: DrawMenu(); break;
            case Phase.GetReady: DrawGetReady(); break;
            case Phase.Playing: DrawPlaying(); break;
            case Phase.Results: DrawResults(); break;
        }

        // Head-position guide: top right, on every screen except the results.
        if (phase != Phase.Results) DrawHeadGuide();

        // Small toggles, bottom left
        if (GUI.Button(new Rect(10, Screen.height - 38, 90, 28), showDebugInfo ? "Debug: on" : "Debug", buttonStyle))
            showDebugInfo = !showDebugInfo;
        if (GUI.Button(new Rect(106, Screen.height - 38, 90, 28), showVideoPreview ? "Video: on" : "Video", buttonStyle))
            showVideoPreview = !showVideoPreview;
        if (showVideoPreview) DrawPreview();
        if (showDebugInfo) DrawDebugPanel();
    }

    // ---------- menu ----------
    void DrawMenu()
    {
        float W = Screen.width, H = Screen.height;
        float w = Mathf.Min(760f, W - 32f), x = Mathf.Round((W - w) / 2f), pad = 22f, iw = w - 2 * pad;
        int t = Mathf.Max(13, Mathf.RoundToInt(guiFontSize * 0.62f));
        float rowH = Mathf.Max(30f, t * 2.2f);

        string blurb = "Henrietta Leavitt discovered that some stars brighten and dim in a steady rhythm. " +
                       "Breathe in as the guide star (left) brightens and out as it dims. Your star (right) follows your breathing.";
        string[] diffText =
        {
            $"Easy: one steady rhythm for the whole game ({easySeconds.x}-{easySeconds.y} s per breath in or out).",
            $"Medium: every breath has a new length, {mediumSeconds.x}-{mediumSeconds.y} s (in and out match).",
            $"Hard: breath-in and breath-out lengths change separately, {hardSeconds.x}-{hardSeconds.y} s.",
        };

        // Measure, then draw the panel behind everything.
        float titleH = style.CalcHeight(new GUIContent("BREATHE WITH THE STAR"), iw);
        float blurbH = smallStyle.CalcHeight(new GUIContent(blurb), iw);
        float h = pad + titleH + 8 + blurbH + 14 + (t + 8) * 3 + (rowH + 6) * 3 + smallStyle.CalcHeight(new GUIContent(diffText[0]), iw) * 2 + 20 + 44 + 30 + pad;
        h = Mathf.Min(h, H - 32f);
        float y0 = Mathf.Round(Mathf.Max(16f, (H - h) / 2f));
        Panel(new Rect(x, y0, w, h));

        float y = y0 + pad;
        style.alignment = TextAnchor.UpperCenter;
        Shadowed(new Rect(x + pad, y, iw, titleH), "BREATHE WITH THE STAR", Gold, style);
        y += titleH + 8;
        smallStyle.alignment = TextAnchor.UpperLeft;
        Shadowed(new Rect(x + pad, y, iw, blurbH), blurb, Color.white, smallStyle);
        y += blurbH + 14;

        // Difficulty
        Label(ref y, x + pad, iw, "DIFFICULTY", t);
        int d = ChoiceRow(ref y, x + pad, iw, rowH, new[] { "Easy", "Medium", "Hard" }, (int)difficulty);
        difficulty = (Difficulty)d;
        Note(ref y, x + pad, iw, diffText[d]);

        // Timer
        Label(ref y, x + pad, iw, "BREATHING TIMER", t);
        timerOn = ChoiceRow(ref y, x + pad, iw, rowH, new[] { "On", "Off" }, timerOn ? 0 : 1) == 0;
        Note(ref y, x + pad, iw, timerOn ? "Shows \"Breathe in 3, 2, 1\" counts." : "No counts: follow the star by eye (harder).");

        // Length
        Label(ref y, x + pad, iw, "LENGTH", t);
        var lengths = new string[lengthChoices.Length];
        for (int i = 0; i < lengths.Length; i++) lengths[i] = Mathf.RoundToInt(lengthChoices[i]) + " s";
        lengthIndex = ChoiceRow(ref y, x + pad, iw, rowH, lengths, Mathf.Clamp(lengthIndex, 0, lengths.Length - 1));
        y += 10;

        // Ready?
        string problem = ConnectionProblem();
        smallStyle.alignment = TextAnchor.UpperCenter;
        Shadowed(new Rect(x + pad, y, iw, 26), problem ?? "Camera is reading your breathing. Ready when you are!",
                 problem == null ? Green : Orange, smallStyle);
        y += 30;

        GUI.enabled = problem == null;
        if (GUI.Button(new Rect(x + pad, y, iw, 44), problem == null ? "START" : "Waiting for the camera...", buttonStyle))
            StartGame();
        GUI.enabled = true;
    }

    void Label(ref float y, float x, float w, string text, int size)
    {
        smallStyle.alignment = TextAnchor.UpperLeft;
        Shadowed(new Rect(x, y, w, size + 6), text, new Color(0.6f, 0.78f, 0.9f), smallStyle);
        y += size + 8;
    }

    void Note(ref float y, float x, float w, string text)
    {
        smallStyle.alignment = TextAnchor.UpperLeft;
        float h = smallStyle.CalcHeight(new GUIContent(text), w);
        Shadowed(new Rect(x, y, w, h), text, new Color(1f, 1f, 1f, 0.75f), smallStyle);
        y += h + 4;
    }

    int ChoiceRow(ref float y, float x, float w, float h, string[] options, int selected)
    {
        float gap = 8f, bw = (w - gap * (options.Length - 1)) / options.Length;
        for (int i = 0; i < options.Length; i++)
        {
            var r = GameFonts.Px(new Rect(x + i * (bw + gap), y, bw, h));
            if (GUI.Toggle(r, i == selected, options[i], toggleStyle) && i != selected) selected = i;
        }
        y += h + 6;
        return selected;
    }

    // ---------- get ready ----------
    void DrawGetReady()
    {
        float W = Screen.width, H = Screen.height;
        int n = Mathf.Max(1, Mathf.CeilToInt(getReadySeconds - (Time.unscaledTime - phaseStart)));
        float w = Mathf.Min(560f, W - 32f), h = 200f;
        var r = new Rect(Mathf.Round((W - w) / 2f), Mathf.Round(H * 0.18f), w, h);
        Panel(r);
        int old = style.fontSize;
        style.fontSize = Mathf.RoundToInt(guiFontSize * 3f);
        style.alignment = TextAnchor.UpperCenter;
        Shadowed(new Rect(r.x, r.y + 14, r.width, style.fontSize + 16), n.ToString(), Gold, style);
        style.fontSize = old;
        smallStyle.alignment = TextAnchor.UpperCenter;
        Shadowed(new Rect(r.x + 16, r.yMax - 64, r.width - 32, 56),
                 "Get ready: breathe in when the left star starts to brighten.", Color.white, smallStyle);
    }

    // ---------- playing ----------
    void DrawPlaying()
    {
        float W = Screen.width;
        float y = 16f;

        // Time left
        int left = Mathf.Max(0, Mathf.CeilToInt(SessionLength - guideTime));
        style.alignment = TextAnchor.UpperCenter;
        Shadowed(new Rect(0, y, W, guiFontSize + 10), string.Format("{0}:{1:00}", left / 60, left % 60), Color.white, style);
        y += guiFontSize + 14;

        // Live "in sync" meter (shown with or without the timer)
        {
            float mw = Mathf.Min(260f, W - 40f), mx = Mathf.Round((W - mw) / 2f);
            smallStyle.alignment = TextAnchor.UpperCenter;
            string label = liveSync < 0f ? "In sync: reading your rhythm..." : $"In sync: {Mathf.RoundToInt(liveSync * 100f)}%";
            Shadowed(new Rect(mx, y, mw, 24), label, Color.white, smallStyle);
            y += 24;
            Fill(new Rect(mx, y, mw, 8), new Color(0, 0, 0, 0.6f));
            if (liveSync >= 0f)
                Fill(new Rect(mx + 1, y + 1, (mw - 2) * liveSync, 6), liveSync >= 0.75f ? Green : liveSync >= 0.45f ? Gold : Orange);
            y += 16;
        }

        // Position / connection problems (the score skips these moments)
        string problem = ConnectionProblem();
        if (problem != null)
        {
            smallStyle.alignment = TextAnchor.UpperCenter;
            float bw = Mathf.Min(900f, W - 40f);
            Panel(new Rect(Mathf.Round((W - bw) / 2f), y - 4, bw, 34), 0.75f);
            Shadowed(new Rect((W - bw) / 2f, y, bw, 28), problem, Orange, smallStyle);
            y += 38;
        }
        else if (SoftWarning)
        {
            smallStyle.alignment = TextAnchor.UpperCenter;
            Shadowed(new Rect(0, y, W, 28), PositionMessage(statusCode, statusHint), Orange, smallStyle);
            y += 32;
        }

        if (timerOn) DrawCoach(y + 6);
    }

    void DrawCoach(float y)
    {
        int secondsLeft = Mathf.Max(1, Mathf.CeilToInt(guideSecondsLeft - 0.001f));
        string word = guideInhale ? "Breathe IN" : "Breathe OUT";
        Color c = guideInhale ? Gold : Color.cyan;
        float w = Mathf.Min(420, Screen.width - 40), x = Mathf.Round((Screen.width - w) / 2);

        int oldSize = style.fontSize;
        style.fontSize = Mathf.RoundToInt(guiFontSize * 1.5f);
        style.alignment = TextAnchor.UpperCenter;
        Shadowed(new Rect(x, y, w, style.fontSize + 12), word + "   " + secondsLeft, c, style);
        style.fontSize = oldSize;

        // Bar = the guide star's brightness: fills as you breathe in, empties as you breathe out.
        float by = y + guiFontSize * 1.5f + 18;
        Fill(new Rect(x, by, w, 10), new Color(0, 0, 0, 0.6f));
        Fill(new Rect(x + 1, by + 1, (w - 2) * guideLevel, 8), c);
    }

    // ---------- results ----------
    void DrawResults()
    {
        float W = Screen.width, H = Screen.height;
        float w = Mathf.Min(720f, W - 32f), pad = 24f, iw = w - 2 * pad;
        var lines = new StringBuilder();
        string settings = $"{difficulty}  ·  timer {(timerOn ? "on" : "off")}  ·  {Mathf.RoundToInt(SessionLength)} s  ·  {result.breaths} breaths";
        if (result.enoughData)
        {
            lines.AppendLine($"Breaths in matched: {result.inhaleMatch:0%}      Breaths out matched: {result.exhaleMatch:0%}");
            lines.AppendLine($"Tracked: {Mathf.RoundToInt(result.tracked * SessionLength)} of {Mathf.RoundToInt(SessionLength)} s");
        }
        var notes = new StringBuilder();
        foreach (string s in result.notes) notes.AppendLine("•  " + s);

        // Online part: Gemini's tip, bests/history and the timeline from Tiger Data.
        string coach = null, bests = null;
        if (serverState == ServerState.Waiting) coach = "Your lab partner is looking at your breathing" + new string('.', 1 + (int)(Time.unscaledTime * 3f) % 3);
        else if (serverState == ServerState.Done && reply != null)
        {
            if (!string.IsNullOrEmpty(reply.coach)) coach = reply.coach;
            if (reply.stored)
            {
                var b = new StringBuilder($"Your best: {reply.yourBest}%   ·   Best today on {difficulty}: {reply.bestToday}%");
                if (reply.history != null && reply.history.Length > 0)
                {
                    b.Append("   ·   Your last games: ");
                    for (int i = 0; i < reply.history.Length && i < 4; i++) b.Append(i > 0 ? ", " : "").Append(reply.history[i].score).Append('%');
                }
                bests = b.ToString();
            }
        }
        bool timeline = serverState == ServerState.Done && reply != null && reply.buckets != null && reply.buckets.Length > 0;
        float coachH = coach != null ? smallStyle.CalcHeight(new GUIContent(coach), iw) : 0f;
        float bestsH = bests != null ? smallStyle.CalcHeight(new GUIContent(bests), iw) : 0f;
        float online = (coach != null ? 26 + coachH + 10 : 0) + (bests != null ? bestsH + 8 : 0) + (timeline ? 26 + 46 : 0);

        float statsH = smallStyle.CalcHeight(new GUIContent(lines.ToString()), iw);
        float notesH = smallStyle.CalcHeight(new GUIContent(notes.ToString()), iw);
        int big = Mathf.RoundToInt(guiFontSize * 2.6f);
        float h = Mathf.Min(H - 32f, pad + guiFontSize + 10 + big + 20 + 30 + 26 + statsH + 12 + 26 + notesH + 16 + online + 44 + pad);
        var r = new Rect(Mathf.Round((W - w) / 2f), Mathf.Round(Mathf.Max(16f, (H - h) / 2f)), w, h);
        Panel(r);

        float y = r.y + pad;
        style.alignment = TextAnchor.UpperCenter;
        Shadowed(new Rect(r.x, y, r.width, guiFontSize + 8), "RESULTS", new Color(0.6f, 0.78f, 0.9f), style);
        y += guiFontSize + 10;

        int old = style.fontSize;
        style.fontSize = big;
        string scoreText = result.enoughData ? result.score + "%" : "--";
        Color scoreColor = result.score >= 80 ? Green : result.score >= 50 ? Gold : Orange;
        Shadowed(new Rect(r.x, y, r.width, big + 12), scoreText, scoreColor, style);
        style.fontSize = old;
        y += big + 14;

        smallStyle.alignment = TextAnchor.UpperCenter;
        Shadowed(new Rect(r.x + pad, y, iw, 26), result.enoughData ? $"in sync with the star  ·  {Rating(result.score)}" : "not enough breathing data",
                 Color.white, smallStyle);
        y += 30;
        Shadowed(new Rect(r.x + pad, y, iw, 24), settings, new Color(1f, 1f, 1f, 0.6f), smallStyle);
        y += 28;

        smallStyle.alignment = TextAnchor.UpperLeft;
        if (statsH > 0) { Shadowed(new Rect(r.x + pad, y, iw, statsH), lines.ToString(), Color.white, smallStyle); y += statsH + 12; }
        Shadowed(new Rect(r.x + pad, y, iw, 24), "NOTES", new Color(0.6f, 0.78f, 0.9f), smallStyle);
        y += 26;
        Shadowed(new Rect(r.x + pad, y, iw, notesH), notes.ToString(), Color.white, smallStyle);
        y += notesH + 12;

        if (coach != null)
        {
            Shadowed(new Rect(r.x + pad, y, iw, 24), "LAB PARTNER", new Color(0.6f, 0.78f, 0.9f), smallStyle);
            y += 26;
            Shadowed(new Rect(r.x + pad, y, iw, coachH), coach, serverState == ServerState.Waiting ? new Color(1f, 1f, 1f, 0.6f) : Gold, smallStyle);
            y += coachH + 10;
        }
        if (bests != null)
        {
            Shadowed(new Rect(r.x + pad, y, iw, bestsH), bests, new Color(1f, 1f, 1f, 0.8f), smallStyle);
            y += bestsH + 8;
        }
        if (timeline)
        {
            // How closely you followed the star in each 5-second stretch (worked out by Tiger Data's time_bucket).
            Shadowed(new Rect(r.x + pad, y, iw, 24), "YOUR GAME, 5 SECONDS AT A TIME", new Color(0.6f, 0.78f, 0.9f), smallStyle);
            y += 26;
            int n = reply.buckets.Length;
            float gap = 4f, bw2 = (iw - gap * (n - 1)) / n, barH = 34f;
            for (int i = 0; i < n; i++)
            {
                var bk = reply.buckets[i];
                var cell = new Rect(r.x + pad + i * (bw2 + gap), y, bw2, barH);
                Fill(cell, new Color(1f, 1f, 1f, 0.06f));
                if (bk.match >= 0f)
                {
                    float m = Mathf.Clamp01(bk.match);
                    Color c = m >= 0.85f ? Green : m >= 0.7f ? Gold : Orange;
                    Fill(new Rect(cell.x, cell.yMax - barH * m, cell.width, barH * m), c);
                }
            }
            y += barH + 12;
        }

        float bw = (iw - 10f) / 2f, by = r.yMax - pad - 44f;
        if (GUI.Button(GameFonts.Px(new Rect(r.x + pad, by, bw, 44)), "Play again", buttonStyle)) StartGame();
        if (GUI.Button(GameFonts.Px(new Rect(r.x + pad + bw + 10f, by, bw, 44)), "Change settings", buttonStyle)) phase = Phase.Menu;
    }

    static string Rating(int score) =>
        score >= 80 ? "in sync" : score >= 60 ? "close" : score >= 40 ? "getting there" : "out of step";

    // ---------- head-position guide ----------
    // A small box standing for the camera's view, a target zone where your head should be,
    // and a dot (plus outline) for your head. Mirrored like a selfie.
    void DrawHeadGuide()
    {
        float gw = Mathf.Min(200f, Screen.width * 0.22f), gh = gw / Mathf.Clamp(frameAspect, 1f, 2.4f);
        var r = GameFonts.Px(new Rect(Screen.width - gw - 16f, 16f, gw, gh));
        Fill(new Rect(r.x - 6, r.y - 6, r.width + 12, r.height + 40), new Color(0, 0, 0, 0.55f));
        Fill(r, new Color(1f, 1f, 1f, 0.05f));
        Outline(r, new Color(1f, 1f, 1f, 0.5f));

        // Target: head roughly centred, in the upper half (so the chest is in view below it).
        var zone = new Rect(r.x + r.width * 0.35f, r.y + r.height * 0.12f, r.width * 0.3f, r.height * 0.45f);
        string hint;
        bool ok;
        if (FaceFresh)
        {
            float mx = 1f - faceX, my = faceY;   // mirrored
            ok = HeadHint(mx, my, faceW, out hint);
            Color c = ok ? Green : Orange;
            Outline(zone, ok ? new Color(0.4f, 1f, 0.5f, 0.6f) : new Color(1f, 1f, 1f, 0.35f));
            var head = new Rect(r.x + (mx - faceW / 2f) * r.width, r.y + (my - faceH / 2f) * r.height, faceW * r.width, faceH * r.height);
            Outline(head, new Color(c.r, c.g, c.b, 0.8f));
            Fill(new Rect(r.x + mx * r.width - 4f, r.y + my * r.height - 4f, 8f, 8f), c);
        }
        else
        {
            Outline(zone, new Color(1f, 1f, 1f, 0.35f));
            ok = statusCode == 0 && DataFlowing;
            hint = !anyPacket ? "Camera not running" : statusCode > 0 ? PositionMessage(statusCode, statusHint) : ok ? "Good position" : "Looking for you...";
        }
        smallStyle.alignment = TextAnchor.UpperCenter;
        int old = smallStyle.fontSize;
        smallStyle.fontSize = Mathf.Max(12, Mathf.RoundToInt(guiFontSize * 0.5f));
        Shadowed(new Rect(r.x - 6, r.yMax + 6, r.width + 12, 30), hint, ok ? Green : Orange, smallStyle);
        smallStyle.fontSize = old;
    }

    // True if the head is well placed; otherwise a short instruction. Coordinates are mirrored, 0-1.
    static bool HeadHint(float mx, float my, float size, out string hint)
    {
        if (size > 0.32f) { hint = "Move back"; return false; }
        if (size < 0.07f) { hint = "Move closer"; return false; }
        if (mx < 0.35f) { hint = "Move to your right"; return false; }
        if (mx > 0.65f) { hint = "Move to your left"; return false; }
        if (my < 0.12f) { hint = "Move down a little"; return false; }
        if (my > 0.57f) { hint = "Sit up / move up a little"; return false; }
        hint = "Good position";
        return true;
    }

    // ---------- debug ----------
    void DrawPreview()
    {
        float pw = Mathf.Min(320, Screen.width * 0.25f);
        float ph = previewTex != null ? pw * previewTex.height / previewTex.width : pw * 0.75f;
        var r = new Rect(Screen.width - pw - 16, Screen.height - ph - 50, pw, ph);
        Fill(new Rect(r.x - 3, r.y - 3, r.width + 6, r.height + 6), new Color(0, 0, 0, 0.6f));
        if (previewTex != null) GUI.DrawTextureWithTexCoords(r, previewTex, new Rect(1, 0, -1, 1)); // mirrored, like a selfie
        if (previewTex == null || Time.unscaledTime - lastPreviewTime > 2f)
        {
            smallStyle.alignment = TextAnchor.UpperCenter;
            Shadowed(new Rect(r.x, r.y + r.height / 2 - 12, r.width, 30), "No camera picture yet", Color.gray, smallStyle);
        }
    }

    void DrawDebugPanel()
    {
        float w = Mathf.Min(460, Screen.width * 0.4f), x = 16, y = 90;
        var sb = new StringBuilder();
        float now = Time.unscaledTime;
        sb.AppendLine("PRESAGE DEBUG");
        sb.AppendLine("Sender: " + (!anyPacket ? "not heard yet" : (now - lastPacketTime < senderTimeoutSeconds ? "connected" : "LOST")) +
                      (senderProcess != null ? "  (started by the game)" : ""));
        sb.AppendLine("Camera frames: " + (pingFrames < 0 ? "?" : pingFrames.ToString()) + (now - lastPreviewTime < 2f ? "  (live)" : ""));
        sb.AppendLine("Metrics callbacks: " + (pingMetrics < 0 ? "?" : pingMetrics.ToString()));
        sb.AppendLine("Waveform samples: " + samples.Count + " buffered" + (DataFlowing ? "  (flowing)" : "  (none recent)"));
        sb.AppendLine("Face: " + (FaceFresh ? string.Format(CultureInfo.InvariantCulture, "x {0:0.00} y {1:0.00} size {2:0.00}", faceX, faceY, faceW) : "none"));
        sb.AppendLine("Status: " + (statusCode < 0 ? "(none)" : statusCode + " " + statusName) +
                      (string.IsNullOrEmpty(statusHint) ? "" : "\n   " + statusHint));
        sb.AppendLine(presageRate > 0
            ? string.Format(CultureInfo.InvariantCulture, "Presage rate: {0:0.0}/min  conf {1:0}%{2}", presageRate, presageConf, presageStable ? " stable" : "")
            : "Presage rate: n/a");
        sb.AppendLine(estimatedRate > 0
            ? string.Format(CultureInfo.InvariantCulture, "Waveform rate: {0:0.0}/min", estimatedRate) : "Waveform rate: n/a (need ~3 breaths)");
        sb.Append("Breath: " + (direction > 0 ? "rising" : direction < 0 ? "falling" : "-") +
                  string.Format(CultureInfo.InvariantCulture, "  level {0:0.00}  guide {1:0.00}", currentNorm, guideLevel));
        if (!string.IsNullOrEmpty(infoText)) sb.Append("\nInfo: " + infoText);
        lock (senderLog) foreach (string l in senderLog) sb.Append("\n> " + l);

        string txt = sb.ToString();
        smallStyle.alignment = TextAnchor.UpperLeft;
        float th = smallStyle.CalcHeight(new GUIContent(txt), w - 16);
        float graphH = 110;
        Fill(new Rect(x, y, w, th + graphH + 30), new Color(0, 0, 0, 0.7f));
        GUI.color = Color.white;
        smallStyle.normal.textColor = Color.white;
        GUI.Label(GameFonts.Px(new Rect(x + 8, y + 6, w - 16, th)), txt, smallStyle);

        // live waveform: last 20 s (yellow line = what the star is showing now)
        var g = new Rect(x + 8, y + th + 14, w - 16, graphH);
        Fill(g, new Color(1, 1, 1, 0.06f));
        if (samples.Count > 1)
        {
            double end = samples[samples.Count - 1].t, start = end - 20.0;
            MinMax(start, end, out float lo, out float hi);
            float range = Mathf.Max(hi - lo, 1e-6f);
            int cols = Mathf.Max(2, Mathf.RoundToInt(g.width / 2));
            for (int i = 0; i < cols; i++)
            {
                double t = start + (end - start) * i / (cols - 1);
                if (t < samples[0].t) continue;
                float n = (ValueAt(t) - lo) / range;
                Fill(new Rect(g.x + i * g.width / cols, g.yMax - n * (g.height - 3) - 3, 2, 3), Color.cyan);
            }
            if (playheadInit)
            {
                float px = (float)((playhead - start) / (end - start));
                if (px >= 0 && px <= 1) Fill(new Rect(g.x + px * g.width, g.y, 1, g.height), Color.yellow);
            }
        }
        else
        {
            smallStyle.alignment = TextAnchor.MiddleCenter;
            GUI.Label(g, "no breathing waveform yet", smallStyle);
        }
    }

    // ---------- GUI helpers ----------
    void EnsureStyles()
    {
        if (style != null) return;
        style = GameFonts.AsTitle(new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true });
        smallStyle = GameFonts.AsBody(new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true });
        buttonStyle = GameFonts.AsBody(new GUIStyle(GUI.skin.button) { fontSize = 16 });
        toggleStyle = GameFonts.AsBody(new GUIStyle(GUI.skin.button) { fontSize = 16 });
        toggleStyle.onNormal.textColor = Gold;
        toggleStyle.onHover.textColor = Gold;
        white = Texture2D.whiteTexture;
    }

    void Panel(Rect r, float alpha = 0.92f)
    {
        r = GameFonts.Px(r);
        Fill(r, new Color(PanelColor.r, PanelColor.g, PanelColor.b, alpha));
        Outline(r, new Color(0.45f, 0.7f, 0.95f, 0.35f));
    }

    void Shadowed(Rect r, string text, Color c, GUIStyle s)
    {
        r = GameFonts.Px(r);
        int o = s.fontSize >= 24 ? 2 : 1;   // thin shadow on small text so it doesn't look fuzzy
        s.normal.textColor = new Color(0, 0, 0, 0.85f);
        GUI.Label(new Rect(r.x + o, r.y + o, r.width, r.height), text, s);
        s.normal.textColor = c;
        GUI.Label(r, text, s);
    }

    void Fill(Rect r, Color c)
    {
        if (Event.current.type != EventType.Repaint) return;
        var old = GUI.color; GUI.color = c;
        GUI.DrawTexture(r, white);
        GUI.color = old;
    }

    void Outline(Rect r, Color c)
    {
        Fill(new Rect(r.x, r.y, r.width, 1), c); Fill(new Rect(r.x, r.yMax, r.width, 1), c);
        Fill(new Rect(r.x, r.y, 1, r.height), c); Fill(new Rect(r.xMax, r.y, 1, r.height), c);
    }

    void OnDestroy()
    {
        running = false;
        client?.Close();
        previewClient?.Close();
        StopSender();
    }

    void OnApplicationQuit() => StopSender();
}
