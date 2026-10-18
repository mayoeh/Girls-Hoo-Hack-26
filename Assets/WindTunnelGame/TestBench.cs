using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class TestBench : MonoBehaviour
{
    [Header("Scene")]
    public RocketModel rocket;
    public Camera cam;
    public FlowLines flow;
    [Tooltip("Material for the tunnel walls and the sting (TunnelMetal).")]
    public Material propMaterial;

    [Header("Modeling tools (hidden while testing)")]
    public NoseEditor editor;
    public ShapeDashboard dashboard;
    public ShapeHistory history;
    public TrialLog trialLog;
    [Tooltip("Gemini feedback after each run. Found automatically if left empty.")]
    public FeedbackCoach coach;

    [Header("Run")]
    [Range(1.6f, 2.2f)] public float targetMach = 2.0f;
    public float spinUpTime = 2.5f, atSpeedTime = 6f, spinDownTime = 2f;
    public Color testBackground = new(0.05f, 0.06f, 0.08f);

    [Header("Tunnel look")]
    public Color backWallColor = new(0.13f, 0.14f, 0.16f);
    public Color gridColor = new(0.24f, 0.26f, 0.3f);
    [Tooltip("Size of one grid square on the back wall (world units).")]
    public float gridCell = 0.1f;

    [Header("Fonts (auto-filled from Assets/Fonts)")]
    [Tooltip("Titles and panel headings")]
    public Font titleFont;
    [Tooltip("All regular text and buttons")]
    public Font bodyFont;

    public enum Mode { Model, Test }
    public enum RunState { Ready, SpinUp, AtSpeed, SpinDown, Results }
    public Mode CurrentMode { get; private set; } = Mode.Model;
    public RunState State { get; private set; } = RunState.Ready;

    float stateTime, speed01, mach, flash, runMach;
    float resultTop, resultBottom, visTop = 0.95f, visBottom = 0.95f;
    ShapeMetrics metrics;
    TrialRecord currentTrial;
    readonly List<TrialRecord> recent = new();
    Vector3 camBase;
    Color camBackground;
    GameObject props;
    System.Action pending;

    GUIStyle panel, head, big, text, note, fireButton, button, tipText;
    GUIStyle tablePanel, tableHead, tableCell, tableCurrent, tableBest, rowHeader, rowEven, rowOdd, rowCurrent;
    readonly List<TrialRecord> tableRows = new();
    Vector2 tableScroll;
    Texture2D white;

    // Saving a design always sends it to the tunnel: you can only go back to modeling after a run.
    bool CanFire => (State == RunState.Ready || State == RunState.Results) && !resultsOpen;
    bool resultsOpen;   // the results pop-up: it must be closed before you can run again or go back to modeling
    bool CanLeave => State == RunState.Results;

    // Fill in anything not assigned in the Inspector, so the modeling panels are always hidden while testing.
    void Awake()
    {
        ShockwaveFonts.Title = titleFont;
        ShockwaveFonts.Body = bodyFont;
        if (rocket == null) rocket = FindFirstObjectByType<RocketModel>();
        if (cam == null) cam = Camera.main;
        if (flow == null) flow = FindFirstObjectByType<FlowLines>();
        if (editor == null) editor = FindFirstObjectByType<NoseEditor>();
        if (dashboard == null) dashboard = FindFirstObjectByType<ShapeDashboard>();
        if (history == null) history = FindFirstObjectByType<ShapeHistory>();
        if (trialLog == null) trialLog = FindFirstObjectByType<TrialLog>();
        if (coach == null) coach = FindFirstObjectByType<FeedbackCoach>();
        if (coach == null) coach = gameObject.AddComponent<FeedbackCoach>();   // works with no setup
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && CurrentMode == Mode.Test && resultsOpen &&
            (kb.escapeKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame)) resultsOpen = false;
        else if (kb != null && CurrentMode == Mode.Test && CanFire && kb.spaceKey.wasPressedThisFrame) Fire();
        if (CurrentMode != Mode.Test) return;

        Step(Time.deltaTime);

        bool showMarker = State == RunState.AtSpeed || State == RunState.SpinDown || State == RunState.Results;
        float paint = State == RunState.SpinDown || State == RunState.Results ? 1f : Smooth(0.3f, 1f, speed01);
        rocket.PaintFlow(visTop, visBottom, paint, showMarker, Time.time);
        if (flow != null) flow.Render(metrics, mach, speed01, visTop, visBottom, Time.deltaTime);
    }

    void LateUpdate()
    {
        if (CurrentMode != Mode.Test || cam == null) return;
        float shake = speed01 * 0.004f + flash * 0.01f;
        float t = Time.time * 25f;
        var offset = new Vector3(Mathf.PerlinNoise(t, 0f) - 0.5f, Mathf.PerlinNoise(0f, t) - 0.5f, 0f) * 2f * shake;
        cam.transform.position = camBase + offset;
    }

    // ---------- modes ----------

    // Called by the trial log's "Save & test" button (and the S key): save the design, then go straight to the tunnel.
    public void SaveAndTest()
    {
        if (CurrentMode != Mode.Model) return;
        currentTrial = trialLog != null ? trialLog.SaveTrial("Saved") : null;
        EnterTest();
    }

    void EnterTest()
    {
        CurrentMode = Mode.Test;
        resultsOpen = false;
        UIPanels.Open("tunnel");   // the tunnel panel always starts open when a test begins
        State = RunState.Ready;
        metrics = ShapeMetrics.Measure(rocket);
        visTop = visBottom = 0.95f;
        SetModelingTools(false);

        camBase = cam.transform.position;
        camBackground = cam.backgroundColor;
        cam.backgroundColor = testBackground;

        BuildProps();
        props.SetActive(true);
    }

    void ExitTest()
    {
        CurrentMode = Mode.Model;
        State = RunState.Ready;
        speed01 = mach = 0f;
        if (flow != null) flow.Hide();
        rocket.ClearFlow();
        if (props != null) props.SetActive(false);

        cam.transform.position = camBase;
        cam.backgroundColor = camBackground;
        SetModelingTools(true);
    }

    void SetModelingTools(bool on)
    {
        if (editor != null) editor.SetEditing(on);
        if (dashboard != null) dashboard.enabled = on;
        if (history != null) history.enabled = on;
        if (trialLog != null) trialLog.enabled = on;
    }

    // ---------- running ----------

    void Fire()
    {
        metrics = ShapeMetrics.Measure(rocket);
        runMach = targetMach;
        float v = Random.Range(-0.02f, 0.02f);
        resultTop = FlowModel.Transition(metrics.top.noseAngle, metrics.top.bluntness, metrics.tipBend, runMach, v);
        resultBottom = metrics.mirrored ? resultTop
            : FlowModel.Transition(metrics.bottom.noseAngle, metrics.bottom.bluntness, metrics.tipBend, runMach, v + Random.Range(-0.01f, 0.01f));

        // The first run uses the trial saved on the way in; each extra run is logged as its own trial.
        if (trialLog != null && (currentTrial == null || currentTrial.hasResult))
            currentTrial = trialLog.SaveTrial("Retest");
        visTop = visBottom = 0.95f;
        Enter(RunState.SpinUp);
    }

    void Step(float dt)
    {
        stateTime += dt;
        flash = Mathf.MoveTowards(flash, 0f, dt * 2f);

        switch (State)
        {
            case RunState.SpinUp:
            {
                float k = Mathf.Clamp01(stateTime / spinUpTime);
                speed01 = k * k * (3f - 2f * k);
                mach = runMach * speed01;
                if (k >= 1f) { Enter(RunState.AtSpeed); flash = 0.8f; }
                break;
            }
            case RunState.AtSpeed:
                speed01 = 1f;
                mach = runMach + (Mathf.PerlinNoise(Time.time * 3f, 0.5f) - 0.5f) * 0.02f;
                if (stateTime >= atSpeedTime) { FinishRun(); Enter(RunState.SpinDown); }
                break;
            case RunState.SpinDown:
            {
                float k = Mathf.Clamp01(stateTime / spinDownTime);
                speed01 = (1f - k) * (1f - k);
                mach = runMach * speed01;
                if (k >= 1f) { Enter(RunState.Results); resultsOpen = true; }
                break;
            }
            default:
                speed01 = 0f;
                mach = 0f;
                break;
        }

        // The transition point creeps forward from the tail as the tunnel comes up to speed, then holds.
        if (State == RunState.SpinUp || State == RunState.AtSpeed)
        {
            float develop = Smooth(0.35f, 1f, speed01);
            visTop = Mathf.Lerp(0.95f, resultTop, develop);
            visBottom = Mathf.Lerp(0.95f, resultBottom, develop);
        }
        else if (State == RunState.Ready)
        {
            visTop = visBottom = 0.95f;
        }
    }

    void FinishRun()
    {
        float result = Mathf.Min(resultTop, resultBottom);
        if (trialLog == null || currentTrial == null) return;
        trialLog.AttachResult(currentTrial, runMach, result);

        recent.Clear();
        for (int i = trialLog.trials.Count - 1; i >= 0 && recent.Count < 3; i--)
        {
            var t = trialLog.trials[i];
            if (t != currentTrial && t.hasResult) recent.Add(t);
        }

        // Ask the lab partner for a tip while the tunnel spins down.
        if (coach != null)
        {
            var tested = new List<TrialRecord>();
            foreach (var t in trialLog.trials) if (t.hasResult) tested.Add(t);   // oldest first
            coach.RequestFeedback(tested);
        }
    }

    void Enter(RunState s)
    {
        State = s;
        stateTime = 0f;
    }

    // ---------- tunnel props ----------

    void BuildProps()
    {
        if (props != null) return;
        props = new GameObject("Tunnel Props");
        props.transform.SetParent(rocket.transform, false);

        float R = rocket.bodyRadius;
        float wall = (flow != null ? flow.wallHeight : 2.4f) * R;
        float x0 = -NoseShape.MaxLength * R - 1.5f, x1 = rocket.bodyLength + 2f;
        const float thick = 0.06f;

        const float depth = 0.5f;   // the back wall sits this far behind the model's centre line
        Prop(PrimitiveType.Cube, "Top Wall", new Vector3((x0 + x1) / 2f, wall + thick / 2f, 0f),
             new Vector3(x1 - x0, thick, 2f * depth), Quaternion.identity);
        Prop(PrimitiveType.Cube, "Bottom Wall", new Vector3((x0 + x1) / 2f, -wall - thick / 2f, 0f),
             new Vector3(x1 - x0, thick, 2f * depth), Quaternion.identity);

        // Back wall with a grid, so the model sits inside the test section instead of between two bars.
        float backHeight = 2f * (wall + thick);
        var back = Prop(PrimitiveType.Cube, "Back Wall", new Vector3((x0 + x1) / 2f, 0f, depth + thick / 2f),
                        new Vector3(x1 - x0, backHeight, thick), Quaternion.identity);
        back.GetComponent<Renderer>().sharedMaterial = MakeGridMaterial(x1 - x0, backHeight);

        // The sting: the rod that holds the model from behind, as in real tunnel tests.
        float stingR = 0.25f * R;
        float sx0 = rocket.bodyLength, sx1 = x1;
        Prop(PrimitiveType.Cylinder, "Sting", new Vector3((sx0 + sx1) / 2f, 0f, 0f),
             new Vector3(2f * stingR, (sx1 - sx0) / 2f, 2f * stingR), Quaternion.Euler(0f, 0f, 90f));

        props.SetActive(false);
    }

    GameObject Prop(PrimitiveType type, string name, Vector3 pos, Vector3 scale, Quaternion rot)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(props.transform, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = rot;
        go.transform.localScale = scale;
        if (propMaterial != null) go.GetComponent<Renderer>().sharedMaterial = propMaterial;
        return go;
    }

    Material MakeGridMaterial(float width, float height)
    {
        var mat = propMaterial != null ? new Material(propMaterial) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
        const int n = 128, cellPx = 32;                 // 4 × 4 grid squares per texture
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                px[y * n + x] = (x % cellPx < 2 || y % cellPx < 2) ? gridColor : backWallColor;
        tex.SetPixels(px);
        tex.Apply();

        mat.SetTexture("_BaseMap", tex);
        mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.2f);
        float tile = gridCell * 4f;                     // world size of one texture repeat
        mat.SetTextureScale("_BaseMap", new Vector2(width / tile, height / tile));
        return mat;
    }

    // ---------- on-screen UI ----------

    void OnGUI()
    {
        EnsureStyles();
        Event e = Event.current;

        if (CurrentMode == Mode.Test)
        {
            GUI.enabled = !resultsOpen;          // the pop-up blocks everything behind it
            DrawTunnelPanel();
            DrawTrialTable();
            GUI.enabled = true;
            if (State == RunState.Results && resultsOpen) DrawResultsPopup(e);

            if (flash > 0f && e.type == EventType.Repaint)
            {
                GUI.color = new Color(1f, 1f, 1f, flash * 0.6f);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), white);
                GUI.color = Color.white;
            }
        }
        else if (coach != null && (coach.Waiting || coach.Text.Length > 0))
        {
            DrawTipPanel(e);   // the last tip stays on screen while you model
        }

        // Button actions run after drawing, so the layout never changes mid-draw.
        if (pending != null)
        {
            var action = pending;
            pending = null;
            action();
        }
    }

    // Short bar across the full width of the top, so the model in the middle stays visible.
    void DrawTunnelPanel()
    {
        GUILayout.BeginArea(new Rect(12f, 12f, Screen.width - 24f, Screen.height * 0.5f));
        GUILayout.BeginVertical(panel);

        if (!UIPanels.Header("tunnel", "WIND TUNNEL", head))
        {
            GUILayout.Label($"M {mach:0.00}   ·   {StateText()}   ·   blue = smooth air, orange = rough air", note);   // still shows what's happening
            GUILayout.EndVertical();
            GUILayout.EndArea();
            return;
        }

        GUILayout.BeginHorizontal();

        // Live speed and status
        GUILayout.BeginVertical(GUILayout.Width(150f));
        GUILayout.Label($"M {mach:0.00}", big);
        GUILayout.Label(StateText(), note);
        GUILayout.Label(LiveText(), text);
        GUILayout.EndVertical();
        GUILayout.Space(12f);

        // Speed slider, fire button, run progress
        bool blocked = !GUI.enabled;   // true while the results pop-up is open
        GUILayout.BeginVertical(GUILayout.MinWidth(200f), GUILayout.MaxWidth(320f));
        GUI.enabled = CanFire;
        GUILayout.Label($"Target Mach   {targetMach:0.00}", text);
        targetMach = Mathf.Round(GUILayout.HorizontalSlider(targetMach, 1.6f, 2.2f) * 20f) / 20f;   // steps of 0.05
        GUILayout.Space(8f);
        if (GUILayout.Button(State == RunState.Results ? "RUN AGAIN  [Space]" : "FIRE  [Space]", fireButton, GUILayout.Height(40f)))
            pending = Fire;
        GUI.enabled = !blocked;
        if (State == RunState.Results)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Show results", button)) pending = () => resultsOpen = true;
            if (GUILayout.Button("Back to modeling", button)) pending = ExitTest;
            GUILayout.EndHorizontal();
        }
        GUILayout.Space(6f);
        Rect progress = GUILayoutUtility.GetRect(180f, 8f, GUILayout.ExpandWidth(true));
        if (Event.current.type == EventType.Repaint) DrawProgress(progress);
        GUILayout.EndVertical();
        GUILayout.Space(16f);

        // Color breakdowns
        GUILayout.BeginVertical(GUILayout.MinWidth(180f), GUILayout.MaxWidth(560f));
        GUILayout.Label("WHAT YOU'RE SEEING", head);
        LegendRow(flow != null ? flow.laminar : new Color(0.35f, 0.75f, 1f), "Blue: smooth air, flowing in neat layers along the model");
        LegendRow(flow != null ? flow.turbulent : new Color(1f, 0.4f, 0.15f), "Orange: rough air that churns and swirls. Keep it as far back as you can");
        LegendRow(Color.white, "White line: where smooth turns rough");
        if (State == RunState.Ready)
            GUILayout.Label("Fire the tunnel to test this design. You can go back to modeling after the run.", note);
        GUILayout.EndVertical();

        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    void LegendRow(Color c, string label)
    {
        GUILayout.BeginHorizontal();
        Rect sw = GUILayoutUtility.GetRect(14f, 14f, GUILayout.Width(14f), GUILayout.Height(14f));
        if (Event.current.type == EventType.Repaint)
        {
            sw.y += 3f;
            GUI.color = c;
            GUI.DrawTexture(sw, white);
            GUI.color = Color.white;
        }
        GUILayout.Space(6f);
        GUILayout.Label(label, note);
        GUILayout.EndHorizontal();
    }

    // Pops up in the middle when a run finishes. Close it (or pick an action) to continue.
    void DrawResultsPopup(Event e)
    {
        float result = Mathf.Min(resultTop, resultBottom);

        // Dim everything behind it
        if (e.type == EventType.Repaint)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), white);
            GUI.color = Color.white;
        }

        float w = Mathf.Min(580f, Screen.width - 48f);
        float h = Mathf.Min(560f, Screen.height - 48f);
        GUILayout.BeginArea(new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h));
        GUILayout.FlexibleSpace();
        GUILayout.BeginVertical(tablePanel);

        GUILayout.BeginHorizontal();
        GUILayout.Label($"RUN COMPLETE  -  {(currentTrial != null ? currentTrial.name : "")}", head);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("×", button, GUILayout.Width(28f))) pending = () => resultsOpen = false;
        GUILayout.EndHorizontal();
        GUILayout.Space(6f);

        GUILayout.Label($"{result:0%}", big);
        GUILayout.Label($"of the model had smooth air, at Mach {runMach:0.00}", text);
        if (!metrics.mirrored)
            GUILayout.Label($"Top {resultTop:0%}   ·   Bottom {resultBottom:0%}   (score uses the worse side)", note);
        GUILayout.Space(4f);
        for (int i = 0; i < recent.Count; i++)
            GUILayout.Label(CompareLine(recent[i], result), note);

        if (coach != null)
        {
            GUILayout.Space(10f);
            GUILayout.Label("LAB PARTNER", head);
            GUILayout.Label(TipText(), tipText);
            GUILayout.Label(coach.Waiting ? " " : coach.Source, note);
        }

        GUILayout.Space(10f);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Run again", button, GUILayout.Height(32f))) pending = () => { resultsOpen = false; Fire(); };
        if (GUILayout.Button("Back to modeling", button, GUILayout.Height(32f))) pending = () => { resultsOpen = false; ExitTest(); };
        if (GUILayout.Button("Close  [Esc]", button, GUILayout.Height(32f))) pending = () => resultsOpen = false;
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUILayout.FlexibleSpace();
        GUILayout.EndArea();
    }

    string TipText() => coach.Waiting ? "Looking at your results" + new string('.', 1 + (int)(Time.time * 3f) % 3) : coach.Text;

    // Modeling mode: the latest tip, top centre, so you can read it while you reshape the nose.
    void DrawTipPanel(Event e)
    {
        const float w = 460f;
        Vector2 origin = new((Screen.width - w) / 2f, 12f);
        GUILayout.BeginArea(new Rect(origin.x, origin.y, w, 220f));
        GUILayout.BeginVertical(panel);
        if (UIPanels.Header("tip", "LAB PARTNER", head))
            GUILayout.Label(TipText(), tipText);
        GUILayout.EndVertical();
        if (e.type == EventType.Repaint)
        {
            Rect r = GUILayoutUtility.GetLastRect();
            r.position += origin;
            UIPanels.Set(this, r);
        }
        GUILayout.EndArea();
    }

    // Bottom of the testing screen: every tested trial, newest first. Always open (no collapse button).
    void DrawTrialTable()
    {
        float w = Mathf.Min(Screen.width - 24f, 940f);
        const float areaHeight = 260f, rowH = 28f;
        const int visibleRows = 5;
        GUILayout.BeginArea(new Rect((Screen.width - w) / 2f, Screen.height - areaHeight - 12f, w, areaHeight));
        GUILayout.FlexibleSpace();
        GUILayout.BeginVertical(tablePanel);
        GUILayout.Label("TEST LOG", head);
        GUILayout.Space(4f);

        tableRows.Clear();
        float best = -1f;
        if (trialLog != null)
            for (int i = trialLog.trials.Count - 1; i >= 0; i--)
            {
                var t = trialLog.trials[i];
                if (!t.hasResult) continue;
                tableRows.Add(t);
                best = Mathf.Max(best, t.smoothFlow);
            }

        TableRow(-1, null, best, rowH);
        if (tableRows.Count == 0)
            GUILayout.Label("No runs yet. Fire the tunnel to add the first one.", tableCell);
        else
        {
            float listHeight = Mathf.Min(tableRows.Count, visibleRows) * rowH;
            tableScroll = GUILayout.BeginScrollView(tableScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar,
                                                    GUIStyle.none, GUILayout.Height(listHeight));
            for (int i = 0; i < tableRows.Count; i++) TableRow(i, tableRows[i], best, rowH);
            GUILayout.EndScrollView();
        }

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    static readonly float[] Cols = { 150f, 70f, 100f, 0f, 80f, 95f, 80f };   // 0 = the bar, which takes the leftover width

    // index -1 = header row.
    void TableRow(int index, TrialRecord t, float best, float rowH)
    {
        bool header = index < 0;
        bool current = t != null && t == currentTrial;
        bool isBest = t != null && Mathf.Approximately(t.smoothFlow, best);
        GUIStyle cell = header ? tableHead : current ? tableCurrent : tableCell;
        GUIStyle row = header ? rowHeader : current ? rowCurrent : index % 2 == 0 ? rowEven : rowOdd;

        GUILayout.BeginHorizontal(row, GUILayout.Height(rowH));
        GUILayout.Label(header ? "TRIAL" : t.name + (isBest ? "   best" : ""), isBest && !header ? tableBest : cell, GUILayout.Width(Cols[0]));
        GUILayout.Label(header ? "MACH" : $"{t.mach:0.00}", cell, GUILayout.Width(Cols[1]));
        GUILayout.Label(header ? "SMOOTH FLOW" : $"{t.smoothFlow:0%}", cell, GUILayout.Width(Cols[2]));
        Rect bar = GUILayoutUtility.GetRect(60f, rowH, GUILayout.ExpandWidth(true));
        GUILayout.Label(header ? "ANGLE" : $"{t.noseAngle:0}°", cell, GUILayout.Width(Cols[4]));
        GUILayout.Label(header ? "BLUNTNESS" : $"{t.bluntness:0.00}", cell, GUILayout.Width(Cols[5]));
        GUILayout.Label(header ? "LENGTH" : $"{t.shape.length:0.00}", cell, GUILayout.Width(Cols[6]));
        GUILayout.EndHorizontal();

        // Bar: how far along the model the flow stayed smooth.
        if (header || Event.current.type != EventType.Repaint) return;
        bar.x += 4f; bar.width -= 16f;
        bar.y += rowH * 0.5f - 5f; bar.height = 10f;
        GUI.color = new Color(1f, 1f, 1f, 0.1f);
        GUI.DrawTexture(bar, white);
        GUI.color = isBest ? new Color(1f, 0.8f, 0.3f) : new Color(0.3f, 0.82f, 1f);
        GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(t.smoothFlow), bar.height), white);
        GUI.color = Color.white;
    }

    string CompareLine(TrialRecord t, float result)
    {
        float d = (result - t.smoothFlow) * 100f;
        return $"vs {t.name}  ({t.smoothFlow:0%} at M{t.mach:0.0}):   {d:+0;-0;0} points";
    }

    string StateText() => State switch
    {
        RunState.Ready => "Ready. Pick a speed and fire.",
        RunState.SpinUp => "Spinning up…",
        RunState.AtSpeed => $"At speed   ·   {Mathf.Max(0f, atSpeedTime - stateTime):0.0} s",
        RunState.SpinDown => "Spinning down…",
        _ => "Run complete.",
    };

    string LiveText() => State switch
    {
        RunState.AtSpeed => $"Smooth flow: {Mathf.Min(visTop, visBottom):0%}",
        RunState.SpinDown or RunState.Results => $"Smooth flow: {Mathf.Min(resultTop, resultBottom):0%}",
        _ => " ",
    };

    void DrawProgress(Rect r)
    {
        float total = spinUpTime + atSpeedTime + spinDownTime;
        float done = State switch
        {
            RunState.SpinUp => stateTime,
            RunState.AtSpeed => spinUpTime + stateTime,
            RunState.SpinDown => spinUpTime + atSpeedTime + stateTime,
            RunState.Results => total,
            _ => 0f,
        };
        GUI.color = new Color(1f, 1f, 1f, 0.08f);
        GUI.DrawTexture(r, white);
        GUI.color = new Color(0.3f, 0.82f, 1f);
        GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(done / total), r.height), white);
        GUI.color = Color.white;
    }

    static float Smooth(float a, float b, float x)
    {
        float t = Mathf.Clamp01((x - a) / (b - a));
        return t * t * (3f - 2f * t);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (!titleFont) titleFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(ShockwaveFonts.TitlePath);
        if (!bodyFont) bodyFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(ShockwaveFonts.BodyPath);
    }
#endif

    void EnsureStyles()
    {
        if (panel != null) return;
        white = Texture2D.whiteTexture;
        var bg = new Texture2D(1, 1);
        bg.SetPixel(0, 0, new Color(0.08f, 0.08f, 0.09f, 0.88f));
        bg.Apply();

        panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(14, 14, 12, 12) };
        panel.normal.background = bg;
        head = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
        head.normal.textColor = new Color(0.6f, 0.75f, 0.85f);
        big = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold };
        big.normal.textColor = Color.white;
        text = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
        text.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
        note = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
        note.normal.textColor = new Color(0.65f, 0.65f, 0.65f);
        tipText = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
        tipText.normal.textColor = Color.white;

        // Test log: solid background and striped rows, so it stays readable over the moving airflow.
        tablePanel = new GUIStyle(panel);
        tablePanel.normal.background = Solid(new Color(0.06f, 0.065f, 0.075f, 0.97f));
        var cellPad = new RectOffset(8, 8, 0, 0);
        tableCell = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15, alignment = TextAnchor.MiddleLeft, padding = cellPad, margin = new RectOffset(0, 0, 0, 0),
            clipping = TextClipping.Clip,
        };
        tableCell.normal.textColor = new Color(0.9f, 0.9f, 0.92f);
        tableHead = new GUIStyle(tableCell) { fontSize = 12, fontStyle = FontStyle.Bold };
        tableHead.normal.textColor = new Color(0.6f, 0.75f, 0.85f);
        tableCurrent = new GUIStyle(tableCell) { fontStyle = FontStyle.Bold };
        tableCurrent.normal.textColor = new Color(0.5f, 0.9f, 1f);
        tableBest = new GUIStyle(tableCell) { fontStyle = FontStyle.Bold };
        tableBest.normal.textColor = new Color(1f, 0.82f, 0.35f);
        rowHeader = RowStyle(new Color(1f, 1f, 1f, 0.06f));
        rowEven = RowStyle(new Color(1f, 1f, 1f, 0.025f));
        rowOdd = RowStyle(new Color(0f, 0f, 0f, 0f));
        rowCurrent = RowStyle(new Color(0.3f, 0.8f, 1f, 0.14f));
        fireButton = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold };
        fireButton.normal.textColor = new Color(1f, 0.55f, 0.4f);
        button = new GUIStyle(GUI.skin.button) { fontSize = 13 };

        ShockwaveFonts.AsTitle(head);
        ShockwaveFonts.AsTitle(big);
        foreach (var s in new[] { text, note, tipText, tableCell, tableHead, tableCurrent, tableBest, fireButton, button })
            ShockwaveFonts.AsBody(s);
    }

    static Texture2D Solid(Color c)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    static GUIStyle RowStyle(Color c)
    {
        var st = new GUIStyle { margin = new RectOffset(0, 0, 0, 0), padding = new RectOffset(0, 0, 0, 0) };
        st.normal.background = Solid(c);
        return st;
    }
}