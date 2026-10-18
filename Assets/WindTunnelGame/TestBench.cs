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

    [Header("Game")]
    [Tooltip("How many different models the player can design and test. Each can be run any number of times.")]
    public int maxModels = 5;

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
    [Tooltip("The NACA label painted on the rocket (Astro Space SDF).")]
    public TMPro.TMP_FontAsset labelFont;

    public enum Mode { Model, Test, Final }
    public enum RunState { Ready, SpinUp, AtSpeed, SpinDown, Results }
    public Mode CurrentMode { get; private set; } = Mode.Model;
    public RunState State { get; private set; } = RunState.Ready;

    float stateTime, speed01, mach, flash, runMach;
    float resultTop, resultBottom, visTop = 0.95f, visBottom = 0.95f;
    ShapeMetrics metrics;
    TrialRecord currentTrial;
    readonly List<TrialRecord> recent = new();
    Vector3 camBase;
    Vector3 camHome;
    float camHomeSize;
    RocketDecor decor;
    Color camBackground;
    GameObject props;
    System.Action pending;

    GUIStyle panel, head, big, text, note, fireButton, button, tipText, tipSmall, score;
    GUIStyle tablePanel, tableHead, tableCell, tableCurrent, tableBest, rowHeader, rowEven, rowOdd, rowCurrent, tabButton;
    readonly List<TrialRecord> tableRows = new();
    Vector2 tableScroll;
    int logModel;
    Texture2D white;

    // Saving a design always sends it to the tunnel: you can only go back to modeling after a run.
    bool CanFire => (State == RunState.Ready || State == RunState.Results) && !resultsOpen;
    bool resultsOpen;   // the results pop-up: it must be closed before you can run again or go back to modeling
    // You can leave once the model in the tunnel has at least one finished run (and nothing is running).
    bool CanLeave => (State == RunState.Ready || State == RunState.Results) && !resultsOpen &&
                     trialLog != null && trialLog.RunCount(currentModel) > 0;

    // Game progress: model 0 is the trial run (the starting sphere), then the player's models 1..maxModels.
    int modelsUsed;      // how many of the player's models have been sent to the tunnel
    int currentModel;    // the model in the tunnel right now
    int currentRun;      // which run of that model
    public int ModelsLeft => Mathf.Max(0, maxModels - modelsUsed);
    bool IsTrialRun => currentModel == 0;
    bool IsLastModel => currentModel >= maxModels;
    string LeaveLabel => IsTrialRun ? "Start designing" : IsLastModel ? "See final results" : "Edit model";

    // Final results (filled when the last model is done).
    readonly List<TrialRecord> finalRows = new();
    TrialRecord finalBest, finalTrial;
    Vector2 finalScroll;

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

        // Fins, roll-pattern label: makes the test model read as a rocket. Added at runtime, no scene setup.
        if (rocket != null)
        {
            decor = rocket.GetComponent<RocketDecor>();
            if (decor == null) decor = rocket.gameObject.AddComponent<RocketDecor>();
            if (labelFont != null && decor.labelFont == null) decor.labelFont = labelFont;
        }
    }

    // Every game starts in the tunnel with the default sphere nose: a forced trial run.
    void Start()
    {
        if (cam != null) { camHome = cam.transform.position; camHomeSize = cam.orthographicSize; }
        StartTrialRun();
    }

    void StartTrialRun()
    {
        modelsUsed = 0;
        rocket.shape = NoseShape.CleanBase(rocket.shape != null ? rocket.shape.points.Count : 3);
        rocket.Rebuild();
        currentModel = 0;
        currentRun = 1;
        currentTrial = trialLog != null ? trialLog.SaveTrial("Trial run", 0, 1) : null;
        EnterTest();
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && !Leaderboard.Typing && CurrentMode == Mode.Test && CanFire && kb.spaceKey.wasPressedThisFrame) Fire();
        if (CurrentMode != Mode.Test) return;

        Step(Time.deltaTime);

        bool showMarker = State == RunState.AtSpeed || State == RunState.SpinDown || State == RunState.Results;
        float paint = State == RunState.SpinDown || State == RunState.Results ? 1f : Smooth(0.3f, 1f, speed01);
        rocket.PaintFlow(visTop, visBottom, paint, showMarker, Time.time);
        if (flow != null) flow.Render(metrics, mach, speed01, visTop, visBottom, Time.deltaTime);
    }

    void LateUpdate()
    {
        if (CurrentMode == Mode.Model) FrameForModeling(Time.unscaledDeltaTime);
        if (decor != null) decor.SetLabel(LabelText());

        // The nose dots belong to the modeling screen only: hide every one of them on any other screen.
        if (CurrentMode != Mode.Model && editor != null)
        {
            if (editor.enabled) editor.SetEditing(false);
            editor.ShowDots(false);
        }
        if (CurrentMode != Mode.Test || cam == null) return;
        float shake = speed01 * 0.004f + flash * 0.01f;
        float t = Time.time * 25f;
        var offset = new Vector3(Mathf.PerlinNoise(t, 0f) - 0.5f, Mathf.PerlinNoise(0f, t) - 0.5f, 0f) * 2f * shake;
        cam.transform.position = camBase + offset;
    }

    // ---------- modes ----------

    // Called by the MODELS panel's "Save & test" button (and the S key): save the design as the next model,
    // then go straight to the tunnel. Each model uses up one of the maxModels tests.
    public void SaveAndTest()
    {
        if (CurrentMode != Mode.Model || ModelsLeft <= 0) return;
        modelsUsed++;
        currentModel = modelsUsed;
        currentRun = 1;
        currentTrial = trialLog != null ? trialLog.SaveTrial("Saved", currentModel, 1) : null;
        EnterTest();
    }

    // "Back to modeling" / "Start designing" / "See final results"
    void Leave()
    {
        resultsOpen = false;
        if (IsLastModel) ShowFinal();
        else ExitTest();
    }

    void EnterTest()
    {
        CurrentMode = Mode.Test;
        resultsOpen = false;
        UIPanels.Open("tunnel");   // the tunnel panels always start open when a test begins
        UIPanels.Open("testlog");
        logModel = currentModel;
        State = RunState.Ready;
        metrics = ShapeMetrics.Measure(rocket);
        visTop = visBottom = 0.95f;
        SetModelingTools(false);

        cam.transform.position = camHome;   // the tunnel always uses the scene's framing
        cam.orthographicSize = camHomeSize;
        camBase = camHome;
        camBackground = cam.backgroundColor;
        cam.backgroundColor = testBackground;

        BuildProps();
        props.SetActive(true);
    }

    void ExitTest()
    {
        LeaveTunnel();
        CurrentMode = Mode.Model;
        SetModelingTools(true);
    }

    // Modeling view: slide the camera so the nose sits in the open space between the top panels
    // (TOOLS / LAB PARTNER / MODELS) and the NOSE SHAPE panel, so no panel covers the dots.
    // It aims at a fixed point near the shoulder (not the tip), so it never moves while you drag.
    void FrameForModeling(float dt)
    {
        if (cam == null || rocket == null || (editor != null && editor.IsDragging)) return;
        float W = Screen.width, H = Screen.height;
        float top = Mathf.Max(UIPanels.ToolsBottom, Mathf.Max(UIPanels.ModelsBottom, UIPanels.TipBottom)) + 8f;
        float bottom = UIPanels.BottomPanelTop < H ? UIPanels.BottomPanelTop - 8f : H - 170f;
        if (bottom - top < 140f) top = Mathf.Max(12f, bottom - 300f);   // tiny window: use what space there is
        float guiY = (top + bottom) * 0.5f;
        float k = 1f - Mathf.Exp(-dt * 8f);

        if (cam.orthographic)
        {
            // Fit the whole rocket (tip to fins) across the screen and into the open band, centred.
            float R = rocket.bodyRadius, scale = rocket.transform.lossyScale.x;
            float xMin = -rocket.NoseLength - 0.5f * R, xMax = rocket.bodyLength + 0.6f * R;
            const float halfHeight = 1.85f;   // body + fins, in body radii
            float band = Mathf.Max(80f, bottom - top);
            float size = Mathf.Max((xMax - xMin) * scale * H / (2f * 0.92f * W),
                                   halfHeight * R * scale * H / (0.9f * band));
            cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, size, k);

            Vector3 centre = rocket.transform.TransformPoint(new Vector3((xMin + xMax) * 0.5f, 0f, 0f));
            Vector3 camPos = cam.transform.position, fwd = cam.transform.forward;
            Vector3 onPlane = centre - fwd * Vector3.Dot(centre - camPos, fwd);   // keep the camera's depth
            float worldPerPixel = 2f * cam.orthographicSize / H;
            Vector3 want = onPlane - cam.transform.up * ((H - guiY) - H * 0.5f) * worldPerPixel;
            cam.transform.position = Vector3.Lerp(camPos, want, k);
            return;
        }

        // Perspective camera: a point a little in front of the shoulder goes to 45% across the screen.
        Vector3 aim = rocket.transform.TransformPoint(new Vector3(-1.2f * rocket.bodyRadius, 0f, 0f));
        float depth = Vector3.Dot(aim - cam.transform.position, cam.transform.forward);
        if (depth <= 0.01f) return;
        Vector3 onScreen = cam.ScreenToWorldPoint(new Vector3(W * 0.45f, H - guiY, depth));
        Vector3 target = cam.transform.position + (aim - onScreen);
        cam.transform.position = Vector3.Lerp(cam.transform.position, target, k);
    }

    // Second line of the label on the rocket.
    string LabelText() => CurrentMode switch
    {
        Mode.Model => $"MODEL {Mathf.Min(modelsUsed + 1, maxModels)}",
        Mode.Final => finalBest == null || finalBest.model == 0 ? "TRIAL RUN" : $"MODEL {finalBest.model}",
        _ => IsTrialRun ? "TRIAL RUN" : $"MODEL {currentModel}",
    };

    // Shared clean-up when leaving the tunnel (to modeling or to the final results).
    void LeaveTunnel()
    {
        State = RunState.Ready;
        speed01 = mach = 0f;
        if (flow != null) flow.Hide();
        rocket.ClearFlow();
        if (props != null) props.SetActive(false);

        cam.transform.position = camBase;
        cam.orthographicSize = camHomeSize;
        cam.backgroundColor = camBackground;
    }

    // After the last model: show the final results table, with the best model on display.
    void ShowFinal()
    {
        LeaveTunnel();
        CurrentMode = Mode.Final;
        SetModelingTools(false);

        finalRows.Clear();
        finalBest = finalTrial = null;
        if (trialLog != null)
        {
            for (int k = 0; k <= maxModels; k++)
            {
                var best = trialLog.BestRun(k);
                if (best == null) continue;
                finalRows.Add(best);
                if (k == 0) finalTrial = best;
                else if (finalBest == null || best.smoothFlow > finalBest.smoothFlow) finalBest = best;
            }
        }
        if (finalBest == null) finalBest = finalTrial;
        if (finalBest != null && finalBest.shape != null)
        {
            rocket.shape = finalBest.shape.Clone();
            rocket.Rebuild();
        }

        // Leaderboard (Tiger Data): the final score is the best design's smooth-flow percentage.
        if (finalBest != null)
            board.NewResult(Mathf.RoundToInt(finalBest.smoothFlow * 100f),
                $"{{\"model\":{finalBest.model},\"mach\":{finalBest.mach.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)},\"models\":{finalRows.Count}}}");
        else board.Refresh();
    }

    Leaderboard boardInstance;
    Leaderboard board => boardInstance ??= new Leaderboard(this, "windtunnel", "", 0);

    void PlayAgain()
    {
        if (trialLog != null) trialLog.ClearAll();
        if (coach != null) coach.Clear();
        recent.Clear();
        finalRows.Clear();
        CurrentMode = Mode.Model;
        StartTrialRun();
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

        // The first run uses the record saved on the way in; each extra run of the same model gets its own record.
        if (trialLog != null && (currentTrial == null || currentTrial.hasResult))
            currentTrial = trialLog.SaveTrial("Retest", currentModel, ++currentRun);
        visTop = visBottom = 0.95f;
        Enter(RunState.SpinUp);

        // Get the panels out of the way so the model is visible; the collapsed runner still shows progress.
        UIPanels.Close("tunnel");
        UIPanels.Close("testlog");
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
                if (k >= 1f)
                {
                    Enter(RunState.Results);
                    resultsOpen = true;
                    UIPanels.Open("tunnel");
                    UIPanels.Open("testlog");
                }
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
        GUI.depth = -10;
        if (e.type == EventType.Layout) UIPanels.TipBottom = 0f;   // re-measured below if the tip panel is shown   // the tunnel UI and pop-ups always draw on top of the modeling panels

        if (CurrentMode == Mode.Final)
        {
            DrawFinalResults();
        }
        else if (CurrentMode == Mode.Test)
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

        if (!UIPanels.Header("tunnel", $"WIND TUNNEL   ·   {ModelText()}", head))
        {
            // Collapsed (e.g. during a run): one status line plus the run's progress bar.
            GUILayout.Label($"Mach {mach:0.00}   ·   {StateText()}   ·   {LiveText()}   ·   blue = smooth air, orange = rough air", note);
            Rect mini = GUILayoutUtility.GetRect(180f, 6f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) DrawProgress(mini);
            GUILayout.EndVertical();
            GUILayout.EndArea();
            return;
        }

        GUILayout.BeginHorizontal();

        // Live speed and status
        GUILayout.BeginVertical(GUILayout.Width(150f));
        GUILayout.Label($"Mach {mach:0.00}", big);
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
        GUILayout.Label($"Mach = x times the speed of sound. Mach 1 is about 1,235 km/h (767 mph)", note);
        GUILayout.Space(6f);
        if (GUILayout.Button("FIRE", fireButton, GUILayout.Height(40f)))
            pending = Fire;
        GUI.enabled = !blocked;
        if (CanLeave)
        {
            GUILayout.BeginHorizontal();
            if (State == RunState.Results && GUILayout.Button("Show results", button)) pending = () => resultsOpen = true;
            if (GUILayout.Button(LeaveLabel, button)) pending = Leave;
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
        if (State == RunState.Ready || State == RunState.Results)
            GUILayout.Label(ReadyText(), State == RunState.Ready && IsTrialRun ? text : note);
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

        GUILayout.Label($"RUN COMPLETE  -  {(currentTrial != null ? currentTrial.name : "")}", head);
        GUILayout.Space(6f);

        GUILayout.Label($"{result:0%}", big);
        GUILayout.Label($"of the model had smooth air, at Mach {runMach:0.00}", text);

        // What could be better, in words (no points or scores here).
        GUILayout.Space(8f);
        GUILayout.Label("WHAT TO TRY NEXT", head);
        foreach (string line in Suggestions(metrics, runMach, resultTop, resultBottom, 2))
            GUILayout.Label("•  " + line, text);

        if (coach != null)
        {
            GUILayout.Space(10f);
            GUILayout.Label("LAB PARTNER", head);
            GUILayout.Label(TipText(), tipText);
            GUILayout.Label(coach.Waiting ? " " : coach.Source, note);
        }

        GUILayout.Space(10f);
        GUILayout.BeginHorizontal();
        // Run again: back to the tunnel screen to pick a new speed, then FIRE.
        if (GUILayout.Button("Run again", button, GUILayout.Height(32f)))
            pending = () => { resultsOpen = false; Enter(RunState.Ready); };
        if (GUILayout.Button(LeaveLabel, button, GUILayout.Height(32f))) pending = Leave;
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUILayout.FlexibleSpace();
        GUILayout.EndArea();
    }

    string TipText() => coach.Waiting ? "Looking at your results" + new string('.', 1 + (int)(Time.time * 3f) % 3) : coach.Text;

    // Modeling mode: the latest tip, in the left column under TOOLS, so it never covers the model,
    // the MODELS panel (right) or the NOSE SHAPE panel (bottom).
    void DrawTipPanel(Event e)
    {
        float w = dashboard != null ? dashboard.toolsWidth : 270f;
        Vector2 origin = new(12f, UIPanels.ToolsBottom + 8f);
        float maxH = Mathf.Min(260f, UIPanels.BottomPanelTop - origin.y - 8f);
        if (maxH < 40f) return;   // no room on a very small window
        GUILayout.BeginArea(new Rect(origin.x, origin.y, w, maxH));
        GUILayout.BeginVertical(panel);
        if (UIPanels.Header("tip", "LAB PARTNER", head))
            GUILayout.Label(TipText(), tipSmall);
        GUILayout.EndVertical();
        if (e.type == EventType.Repaint)
        {
            Rect r = GUILayoutUtility.GetLastRect();
            r.position += origin;
            UIPanels.Set(this, r);
            UIPanels.TipBottom = r.yMax;
        }
        GUILayout.EndArea();
    }

    // ---------- final results ----------

    // After the last model: score, a short summary, and every model's best run.
    void DrawFinalResults()
    {
        Event e = Event.current;
        if (e.type == EventType.Repaint)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), white);
            GUI.color = Color.white;
        }

        float w = Mathf.Min(900f, Screen.width - 32f);
        float h = Screen.height - 32f;
        GUILayout.BeginArea(new Rect((Screen.width - w) / 2f, 16f, w, h));
        GUILayout.FlexibleSpace();
        GUILayout.BeginVertical(tablePanel);
        finalScroll = GUILayout.BeginScrollView(finalScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar,
                                                GUIStyle.none, GUILayout.MaxHeight(h - 40f));

        GUILayout.Label("FINAL RESULTS", head);
        GUILayout.Space(4f);

        if (finalBest == null)
        {
            GUILayout.Label("No runs were recorded.", text);
        }
        else
        {
            int scoreValue = Mathf.RoundToInt(finalBest.smoothFlow * 100f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("SCORE", head, GUILayout.Width(70f));
            GUILayout.Label($"{scoreValue}", score);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label($"Your best design, {finalBest.name}, kept {finalBest.smoothFlow:0%} of the model in smooth air " +
                            $"(at Mach {finalBest.mach:0.00}). Your score is that percentage.", text);
            GUILayout.Space(8f);

            GUILayout.Label("HOW YOU DID", head);
            foreach (string line in FinalSummary()) GUILayout.Label("•  " + line, text);
            GUILayout.Space(10f);

            GUILayout.Label("YOUR MODELS  (best run of each)", head);
            GUILayout.Space(2f);
            FinalRow(null, -1);
            for (int i = 0; i < finalRows.Count; i++) FinalRow(finalRows[i], i);
        }

        GUILayout.Space(14f);
        board.DrawLayout(w - 60f, head, text, fireButton);

        GUILayout.Space(12f);
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Play again", fireButton, GUILayout.Width(220f), GUILayout.Height(40f))) pending = PlayAgain;
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
        GUILayout.FlexibleSpace();
        GUILayout.EndArea();
    }

    List<string> FinalSummary()
    {
        var lines = new List<string>();
        if (finalTrial != null && finalBest != finalTrial)
        {
            float gain = (finalBest.smoothFlow - finalTrial.smoothFlow) * 100f;
            lines.Add(gain > 0.5f
                ? $"The starting sphere kept {finalTrial.smoothFlow:0%} smooth. Your best model beat it by {gain:0} points."
                : $"The starting sphere kept {finalTrial.smoothFlow:0%} smooth. None of your models beat it this time.");

            var changes = new List<string>();
            if (finalBest.bluntness < finalTrial.bluntness - 0.1f)
                changes.Add($"a sharper tip (bluntness {finalTrial.bluntness:0.00} → {finalBest.bluntness:0.00})");
            if (finalBest.noseAngle < finalTrial.noseAngle - 5f)
                changes.Add($"a slimmer nose ({finalTrial.noseAngle:0}° → {finalBest.noseAngle:0}°)");
            if (changes.Count > 0)
                lines.Add("What made the difference: " + string.Join(" and ", changes) + ".");
        }

        int tested = 0;
        foreach (var r in finalRows) if (r.model > 0) tested++;
        lines.Add($"You tested {tested} of {maxModels} models.");

        if (finalBest.smoothFlow < 0.83f && finalBest.shape != null)
        {
            var m = rocket.MeasureShape(finalBest.shape);
            lines.Add("To score higher: " + Suggestions(m, finalBest.mach, finalBest.smoothFlow, finalBest.smoothFlow, 1)[0]);
        }
        lines.Add("Possible range in this tunnel: about 10% at worst (a wide, fully rounded, bent nose at Mach 1.6) " +
                  "to about 85% at best (a sharp, slim, straight nose). Mary Jackson's tests found the same pattern: " +
                  "blunter, wider noses make smooth air turn rough sooner.");
        return lines;
    }

    static readonly float[] FinalCols = { 140f, 0f, 70f, 80f, 70f, 90f, 60f };   // 0 = the bar

    void FinalRow(TrialRecord t, int index)
    {
        bool header = t == null;
        bool isBest = t != null && t == finalBest;
        GUIStyle cell = header ? tableHead : isBest ? tableBest : tableCell;
        GUIStyle row = header ? rowHeader : index % 2 == 0 ? rowEven : rowOdd;
        const float rowH = 28f;

        GUILayout.BeginHorizontal(row, GUILayout.Height(rowH));
        GUILayout.Label(header ? "MODEL" : (t.model == 0 ? "Trial run" : $"Model {t.model}") + (isBest ? "   best" : ""), cell, GUILayout.Width(FinalCols[0]), GUILayout.Height(rowH));
        Rect bar = GUILayoutUtility.GetRect(60f, rowH, GUILayout.ExpandWidth(true));
        GUILayout.Label(header ? "SMOOTH" : $"{t.smoothFlow:0%}", cell, GUILayout.Width(FinalCols[2]), GUILayout.Height(rowH));
        GUILayout.Label(header ? "MACH" : $"{t.mach:0.00}", cell, GUILayout.Width(FinalCols[3]), GUILayout.Height(rowH));
        GUILayout.Label(header ? "ANGLE" : $"{t.noseAngle:0}°", cell, GUILayout.Width(FinalCols[4]), GUILayout.Height(rowH));
        GUILayout.Label(header ? "BLUNTNESS" : $"{t.bluntness:0.00}", cell, GUILayout.Width(FinalCols[5]), GUILayout.Height(rowH));
        GUILayout.Label(header ? "RUNS" : $"{(trialLog != null ? trialLog.RunCount(t.model) : 1)}", cell, GUILayout.Width(FinalCols[6]), GUILayout.Height(rowH));
        GUILayout.EndHorizontal();

        if (header || Event.current.type != EventType.Repaint) return;
        bar.x += 4f; bar.width -= 12f;
        bar.y += rowH * 0.5f - 5f; bar.height = 10f;
        GUI.color = new Color(1f, 1f, 1f, 0.1f);
        GUI.DrawTexture(bar, white);
        GUI.color = isBest ? new Color(1f, 0.8f, 0.3f) : new Color(0.3f, 0.82f, 1f);
        GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(t.smoothFlow), bar.height), white);
        GUI.color = Color.white;
    }

    // Bottom of the testing screen: one tab per model, showing that model's runs (newest first).
    // Short on purpose (3 rows, then it scrolls) so the model stays visible. Collapses during a run.
    void DrawTrialTable()
    {
        float w = Mathf.Min(Screen.width - 24f, 760f);
        const float areaHeight = 230f, rowH = 28f;
        const int visibleRows = 3;
        GUILayout.BeginArea(new Rect((Screen.width - w) / 2f, Screen.height - areaHeight - 12f, w, areaHeight));
        GUILayout.FlexibleSpace();
        GUILayout.BeginVertical(tablePanel);
        if (!UIPanels.Header("testlog", "TEST LOG", head))
        {
            GUILayout.EndVertical();
            GUILayout.EndArea();
            return;
        }

        // Tabs: the trial run and every model that has been tested.
        GUILayout.Space(2f);
        GUILayout.BeginHorizontal();
        for (int k = 0; k <= maxModels; k++)
        {
            if (trialLog == null || trialLog.RunCount(k) == 0) continue;
            bool on = k == logModel;
            string label = k == 0 ? "Trial run" : $"Model {k}";
            if (GUILayout.Toggle(on, label, tabButton, GUILayout.Height(24f)) && !on) logModel = k;
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(4f);

        tableRows.Clear();
        float best = -1f;
        TrialRecord shapeOf = null;
        if (trialLog != null)
            for (int i = trialLog.trials.Count - 1; i >= 0; i--)
            {
                var t = trialLog.trials[i];
                if (t.model != logModel) continue;
                shapeOf ??= t;
                if (!t.hasResult) continue;
                tableRows.Add(t);
                best = Mathf.Max(best, t.smoothFlow);
            }

        // The shape is the same for every run of a model, so it's shown once.
        if (shapeOf != null)
            GUILayout.Label($"Angle {shapeOf.noseAngle:0}°   ·   Bluntness {shapeOf.bluntness:0.00}   ·   Length {shapeOf.shape.length:0.00}", note);

        TableRow(-1, null, best, rowH);
        if (tableRows.Count > 0)   // before the first run it's just the empty table
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

    static readonly float[] Cols = { 120f, 80f, 120f, 0f };   // 0 = the bar, which takes the leftover width

    // index -1 = header row. Best run = gold text, the run just done = blue text (colour only).
    void TableRow(int index, TrialRecord t, float best, float rowH)
    {
        bool header = index < 0;
        bool current = t != null && t == currentTrial;
        bool isBest = t != null && Mathf.Approximately(t.smoothFlow, best) && tableRows.Count > 1;
        GUIStyle cell = header ? tableHead : isBest ? tableBest : current ? tableCurrent : tableCell;
        GUIStyle row = header ? rowHeader : current ? rowCurrent : index % 2 == 0 ? rowEven : rowOdd;

        GUILayout.BeginHorizontal(row, GUILayout.Height(rowH));
        GUILayout.Label(header ? "RUN" : $"Run {Mathf.Max(1, t.run)}" + (isBest ? "  (best)" : ""), cell, GUILayout.Width(Cols[0]), GUILayout.Height(rowH));
        GUILayout.Label(header ? "MACH" : $"{t.mach:0.00}", cell, GUILayout.Width(Cols[1]), GUILayout.Height(rowH));
        GUILayout.Label(header ? "SMOOTH FLOW" : $"{t.smoothFlow:0%}", cell, GUILayout.Width(Cols[2]), GUILayout.Height(rowH));
        Rect bar = GUILayoutUtility.GetRect(60f, rowH, GUILayout.ExpandWidth(true));
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

    string ModelText() => IsTrialRun
        ? (currentRun > 1 ? $"TRIAL RUN  #{currentRun}" : "TRIAL RUN")
        : $"MODEL {currentModel} OF {maxModels}" + (currentRun > 1 ? $"  ·  RUN {currentRun}" : "");

    string ReadyText()
    {
        if (IsTrialRun)
            return State == RunState.Ready
                ? "Trial run: this is the starting sphere nose. Fire the tunnel to see how the air flows over it. " +
                  $"After that you'll design {maxModels} models of your own."
                : $"When you're ready, press Start designing. You have {maxModels} models to test.";
        if (IsLastModel)
            return "This is your last model. Run it as many times as you like, then see your final results.";
        return $"Run this model as many times as you like (try different speeds). {ModelsLeft} model tests left after this one.";
    }

    // Plain-language hints about what holds this shape back, worst first. Mirrors FlowModel's rules.
    static List<string> Suggestions(ShapeMetrics m, float mach, float top, float bottom, int max)
    {
        float lowSpeed = Mathf.Lerp(1f, 0.3f, (mach - 1.6f) / 0.6f);
        var items = new List<(float cost, string text)>
        {
            (0.45f * m.Bluntness * lowSpeed,
             "The rounded tip is what trips the air first. A sharper point keeps it smooth for longer." +
             (mach < 2.15f && m.Bluntness > 0.2f ? " Round tips also do a little better at higher speeds." : "")),
            (0.10f * (Mathf.Clamp(m.Angle, 10f, 60f) - 10f) / 50f,
             "A slimmer, more pointed nose (a smaller angle) would help a little."),
            (0.003f * Mathf.Abs(m.tipBend),
             "The tip is bent up or down. A straight tip lets the air stay smooth longer."),
        };
        if (!m.mirrored && Mathf.Abs(top - bottom) > 0.02f)
            items.Add((Mathf.Abs(top - bottom),
                       $"The {(top < bottom ? "top" : "bottom")} side turned rough first. Shaping both sides the same (Mirror: ON) helps."));
        items.Sort((a, b) => b.cost.CompareTo(a.cost));

        var lines = new List<string>();
        foreach (var it in items)
            if (it.cost > 0.015f && lines.Count < max) lines.Add(it.text);
        if (lines.Count == 0)
            lines.Add("This shape is about as smooth as this tunnel allows. Nice work!");
        return lines;
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
        if (!bodyFont || GameFonts.IsOldFont(bodyFont)) bodyFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(ShockwaveFonts.BodyPath);
        if (!labelFont) labelFont = UnityEditor.AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>("Assets/Fonts/AstroSpace SDF.asset");
    }
#endif

    void EnsureStyles()
    {
        if (panel != null) return;
        white = Texture2D.whiteTexture;
        var bg = new Texture2D(1, 1);
        bg.SetPixel(0, 0, new Color(0.08f, 0.08f, 0.09f, 0.97f));
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
        tipSmall = new GUIStyle(tipText) { fontSize = 13 };
        score = new GUIStyle(GUI.skin.label) { fontSize = 48, fontStyle = FontStyle.Bold };
        score.normal.textColor = new Color(1f, 0.82f, 0.35f);

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
        tableHead = new GUIStyle(tableCell) { fontSize = 12, fontStyle = FontStyle.Bold, padding = new RectOffset(8, 8, 2, 0) };
        tableHead.normal.textColor = new Color(0.6f, 0.75f, 0.85f);
        tableCurrent = new GUIStyle(tableCell);
        tableCurrent.normal.textColor = new Color(0.5f, 0.9f, 1f);
        tableBest = new GUIStyle(tableCell);
        tableBest.normal.textColor = new Color(1f, 0.82f, 0.35f);
        rowHeader = RowStyle(new Color(1f, 1f, 1f, 0.06f));
        rowEven = RowStyle(new Color(1f, 1f, 1f, 0.025f));
        rowOdd = RowStyle(new Color(0f, 0f, 0f, 0f));
        rowCurrent = RowStyle(new Color(0.3f, 0.8f, 1f, 0.14f));
        fireButton = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold };
        fireButton.normal.textColor = new Color(1f, 0.55f, 0.4f);
        button = new GUIStyle(GUI.skin.button) { fontSize = 13 };
        tabButton = new GUIStyle(GUI.skin.button) { fontSize = 13, padding = new RectOffset(12, 12, 4, 4) };
        tabButton.onNormal.textColor = new Color(0.5f, 0.9f, 1f);

        ShockwaveFonts.AsTitle(head);
        ShockwaveFonts.AsTitle(big);
        ShockwaveFonts.AsTitle(score);
        foreach (var s in new[] { text, note, tipText, tipSmall, tableCell, tableHead, tableCurrent, tableBest, fireButton, button, tabButton })
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
