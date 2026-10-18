using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class SatelliteGame : MonoBehaviour
{
    enum State { Title, Setup, Trial, TrialDone, Countdown, Round, Caught, Results }

    [Header("References (auto-found if empty)")]
    public ArmTest arm;
    [Tooltip("Point on the gripper that does the grabbing (GripArea)")]
    public Transform gripPoint;
    [Tooltip("The satellite object in the scene")]
    public Transform satellite;
    [Tooltip("Optional: an empty child of the satellite marking the part to grab. Empty = top centre of the satellite.")]
    public Transform satelliteGrabPoint;
    public Camera cam;

    [Header("Game flow")]
    public int rounds = 3;
    public float countdownSeconds = 3f;
    [Tooltip("Pause before a missed satellite comes back")]
    public float respawnDelay = 1.5f;
    public float caughtPause = 2f;

    [Header("Difficulty")]
    [Tooltip("Margin of error: how close (world units) the gripper must be to the grab point. 0 = auto (10% of the arm's reach).")]
    public float catchMargin = 0f;
    [Tooltip("The margin is multiplied by this every round. 1 = same every round, 0.8 = 20% tighter each round.")]
    [Range(0.3f, 1f)] public float marginScalePerRound = 0.85f;
    [Tooltip("Trial margin = catchMargin x this")]
    public float trialMarginMultiplier = 1.5f;
    [Tooltip("Round 1: seconds a satellite takes to drift from the screen edge to its catch point. Lower = faster.")]
    public float entrySeconds = 7f;
    [Tooltip("Satellite speed is multiplied by this every round")]
    public float speedScalePerRound = 1.2f;
    [Tooltip("How far below the grab point the gripper may be and still count as 'above', as a fraction of the margin")]
    [Range(0f, 1f)] public float belowTolerance = 0.3f;
    public float spinDegreesPerSecond = 10f;

    [Header("Fonts (auto-filled from Assets/Fonts)")]
    [Tooltip("Titles and big headings")]
    public Font titleFont;
    [Tooltip("All regular text and buttons")]
    public Font bodyFont;

    [Header("Phone setup screen")]
    [Tooltip("QR code for the controller page (auto-filled from Assets/ArmGame/ControllerQR.png).")]
    public Texture2D controllerQr;
    [Tooltip("The address the QR code opens. The QR is only shown when this matches ArmTest's relay address.")]
    public string controllerQrUrl = "https://149-28-227-46.sslip.io/controller";

    [Header("Colors")]
    public Color outlineColor = new Color(0.3f, 0.85f, 1f, 1f);
    public Color readyColor = new Color(0.3f, 1f, 0.45f, 1f);

    // ---------- runtime ----------
    State state;
    float stateTime;
    int round;                       // 0-based
    float roundTime;
    bool timing;
    readonly List<float> times = new List<float>();
    readonly List<int> missedGrabs = new List<int>();
    readonly List<int> gotAway = new List<int>();

    bool held;
    bool satVisible;
    bool seenThisPass;
    float respawnIn = -1f;
    float spawnAge;
    Vector3 topPos;                  // world position of the grab point while drifting
    Vector3 velocity;
    Vector3 trialTarget;
    float spin;

    Quaternion uprightRot;
    Vector3 grabLocal;               // grab point offset from the satellite pivot (unrotated)
    float reach = 1f, satRadius = 1f;

    GameObject ghost;
    Material ghostMat, lineMat;
    LineRenderer ringFlat, ringFacing, approachLine, gripRing;

    string message = "";
    float messageUntil;
    GUIStyle big, mid, small, smallRight, button, instructions;
    int styleHeight;

    static SatelliteGame instance;

    // ================= setup =================

    void Awake()
    {
        if (instance != null && instance != this) { Destroy(this); return; }
        instance = this;

        if (!arm) arm = FindFirstObjectByType<ArmTest>();
        if (!gripPoint) { var g = GameObject.Find("GripArea"); if (g) gripPoint = g.transform; }
        if (!satellite) { var s = GameObject.Find("Satellite"); if (s) satellite = s.transform; }
        if (!cam) cam = Camera.main;

        if (!arm || !gripPoint || !satellite || !cam)
        {
            Debug.LogError($"SatelliteGame: missing reference (arm={arm}, gripPoint={gripPoint}, satellite={satellite}, camera={cam}). Drag them into the Inspector.");
            enabled = false;
            return;
        }
        if (transform.IsChildOf(satellite))
            Debug.LogError("SatelliteGame: put this script on its own empty GameObject, not on the satellite.");

        arm.showDebugGui = false;
        arm.GripPressed += OnGripPressed;
        arm.PhoneCalibrated += OnPhoneCalibrated;
    }

    void OnDestroy()
    {
        if (arm) { arm.GripPressed -= OnGripPressed; arm.PhoneCalibrated -= OnPhoneCalibrated; }
        if (instance == this) instance = null;
    }

    void Start()
    {
        MeasureSatellite();
        MeasureArm();
        if (catchMargin <= 0f) catchMargin = Mathf.Max(0.01f, Mathf.Round(reach * 0.1f * 100f) / 100f);

        lineMat = new Material(Shader.Find("Sprites/Default"));
        ringFlat     = MakeLine("Outline Ring", 64, true);
        ringFacing   = MakeLine("Outline Ring 2", 64, true);
        approachLine = MakeLine("Approach Line", 2, false);
        gripRing     = MakeLine("Grip Marker", 32, true);
        BuildGhost();

        EnterTitle();
    }

    void MeasureSatellite()
    {
        uprightRot = satellite.rotation;
        Bounds b = new Bounds(satellite.position, Vector3.zero);
        bool any = false;
        foreach (var r in satellite.GetComponentsInChildren<Renderer>())
        {
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        if (!any) b = new Bounds(satellite.position, Vector3.one);
        satRadius = Mathf.Max(b.extents.magnitude, 0.01f);

        Vector3 grabWorld = satelliteGrabPoint
            ? satelliteGrabPoint.position
            : new Vector3(b.center.x, b.max.y, b.center.z);   // top centre
        grabLocal = Quaternion.Inverse(uprightRot) * (grabWorld - satellite.position);
    }

    void MeasureArm()
    {
        Transform p = arm.pitchJoint.segment, w = arm.wristJoint.segment;
        float r = 0f;
        if (p && w) r += Vector3.Distance(p.position, w.position);
        if (w) r += Vector3.Distance(w.position, gripPoint.position);
        else if (p) r += Vector3.Distance(p.position, gripPoint.position);
        reach = r > 0.0001f ? r : 1f;
    }

    LineRenderer MakeLine(string name, int count, bool loop)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.loop = loop;
        lr.positionCount = count;
        lr.material = lineMat;
        lr.widthMultiplier = reach * 0.006f;
        lr.shadowCastingMode = ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.numCapVertices = 2;
        lr.enabled = false;
        return lr;
    }

    void BuildGhost()
    {
        ghostMat = MakeTransparentMaterial();
        ghost = Instantiate(satellite.gameObject);
        ghost.name = "Satellite Outline (runtime)";
        foreach (var c in ghost.GetComponentsInChildren<Collider>(true)) Destroy(c);
        foreach (var rb in ghost.GetComponentsInChildren<Rigidbody>(true)) Destroy(rb);
        foreach (var mb in ghost.GetComponentsInChildren<MonoBehaviour>(true)) Destroy(mb);
        foreach (var r in ghost.GetComponentsInChildren<Renderer>(true))
        {
            var mats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = ghostMat;
            r.sharedMaterials = mats;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
        }
        ghost.SetActive(false);
    }

    static Material MakeTransparentMaterial()
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) return new Material(Shader.Find("Sprites/Default"));

        var m = new Material(sh);
        m.SetFloat("_Surface", 1f);   // transparent
        m.SetFloat("_Blend", 0f);     // alpha
        m.SetOverrideTag("RenderType", "Transparent");
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_SrcBlendAlpha")) m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        if (m.HasProperty("_DstBlendAlpha")) m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)RenderQueue.Transparent;
        return m;
    }

    // ================= state changes =================

    void SetState(State s) { state = s; stateTime = 0f; }

    void EnterTitle()
    {
        SetState(State.Title);
        round = 0;
        times.Clear(); missedGrabs.Clear(); gotAway.Clear();
        spin = 0f;
        held = true;
        ShowSatellite(true);
    }

    // Phone setup: connect and calibrate before the trial can start.
    bool setupCalibrated;

    void EnterSetup()
    {
        SetState(State.Setup);
        setupCalibrated = false;   // calibrate again each game, so the phone's "straight ahead" is right
    }

    void OnPhoneCalibrated()
    {
        if (state == State.Setup) setupCalibrated = true;
    }

    bool SetupReady => arm.PhoneConnected && setupCalibrated;

    void BeginTrial()
    {
        SetState(State.Trial);
        spin = 0f;
        held = true;
        ShowSatellite(true);
        trialTarget = SampleReachablePoint(gripPoint.position, reach * 0.35f);
        Flash("Move the satellite into the outline, then click to release it.");
    }

    void BeginCountdown()
    {
        SetState(State.Countdown);
        held = false;
        ShowSatellite(false);
    }

    void BeginRound()
    {
        SetState(State.Round);
        roundTime = 0f;
        timing = false;
        missedGrabs.Add(0);
        gotAway.Add(0);
        Spawn();
    }

    void NextRound()
    {
        round++;
        held = false;
        if (round >= rounds) { ShowSatellite(false); SetState(State.Results); }
        else BeginCountdown();
    }

    // ================= update =================

    void Update()
    {
        float dt = Time.deltaTime;
        stateTime += dt;
        if (Pressed("c")) { arm.Calibrate(); if (state == State.Setup && arm.PhoneConnected) setupCalibrated = true; }

        switch (state)
        {
            case State.Title:
                if (Pressed("space")) EnterSetup();
                break;
            case State.Setup:
                if (Pressed("space") && SetupReady) BeginTrial();
                if (Pressed("skip")) BeginTrial();   // Shift+S: developer skip, for testing without a phone
                break;
            case State.TrialDone:
                Drift(dt);
                if (stateTime > 2.5f) BeginCountdown();
                break;
            case State.Countdown:
                if (stateTime >= countdownSeconds) BeginRound();
                break;
            case State.Round:
                UpdateRound(dt);
                break;
            case State.Caught:
                if (stateTime >= caughtPause) NextRound();
                break;
            case State.Results:
                if (Pressed("space") || Pressed("r")) EnterTitle();
                break;
        }
        arm.gripClosed = held;
    }

    void UpdateRound(float dt)
    {
        if (timing) roundTime += dt;   // the clock keeps running through respawns

        if (respawnIn > 0f)
        {
            respawnIn -= dt;
            if (respawnIn <= 0f) Spawn();
            return;
        }

        Drift(dt);
        spawnAge += dt;

        bool visible = InView(topPos, -0.1f);
        if (visible && !seenThisPass) { seenThisPass = true; timing = true; }

        bool leftView = seenThisPass && !visible;
        bool neverArrived = spawnAge > entrySeconds * 4f;
        if (leftView || neverArrived)
        {
            ShowSatellite(false);
            gotAway[round]++;
            respawnIn = respawnDelay;
            Flash("It drifted away. Another one is coming!");
        }
    }

    void Drift(float dt)
    {
        topPos += velocity * dt;
        spin += spinDegreesPerSecond * dt;
        PoseSatellite(topPos);
    }

    void Spawn()
    {
        Vector3 catchPoint = SampleReachablePoint(gripPoint.position, reach * 0.3f);

        Vector3 dir = Vector3.right;
        for (int i = 0; i < 60; i++)
        {
            dir = Random.onUnitSphere;
            if (Mathf.Abs(Vector3.Dot(dir, cam.transform.forward)) < 0.5f) break;
        }

        // 3. Walk back along the path until just off screen
        float step = Mathf.Max(reach * 0.05f, 0.001f), s = 0f, max = reach * 80f;
        while (s < max && InView(catchPoint - dir * s, -0.05f)) s += step;
        s += satRadius * 1.2f;

        float speed = s / Mathf.Max(0.5f, entrySeconds) * Mathf.Pow(speedScalePerRound, round);
        velocity = dir * speed;
        topPos = catchPoint - dir * s;
        spin = Random.Range(0f, 360f);
        spawnAge = 0f;
        seenThisPass = false;
        respawnIn = -1f;
        ShowSatellite(true);
        PoseSatellite(topPos);
    }

    // ================= grabbing / releasing =================

    void OnGripPressed(Vector2 mouse)
    {

        float m = Margin();
        switch (state)
        {
            case State.Trial:
            {
                float d = Vector3.Distance(gripPoint.position, trialTarget);
                if (d <= m)
                {
                    held = false;
                    topPos = gripPoint.position;
                    velocity = (Vector3.up * 0.6f + Random.insideUnitSphere * 0.3f).normalized * reach * 0.12f;
                    SetState(State.TrialDone);
                    Flash("Perfect release! Get ready for Round 1");
                }
                else Flash($"Not lined up yet: {d:0.00} away (needs {m:0.00}). Keep moving into the outline");
                break;
            }
            case State.Round:
            {
                if (!satVisible || respawnIn > 0f) return;
                if (InCatchZone(out float horiz, out float vert))
                {
                    held = true;
                    timing = false;
                    times.Add(roundTime);
                    SetState(State.Caught);
                    Flash($"CAUGHT! Round {round + 1}: {roundTime:0.00} s");
                }
                else
                {
                    missedGrabs[round]++;
                    Flash(vert < -m * belowTolerance ? "Missed! Get ABOVE the satellite." : "Missed! Line up over the glowing ring.");
                }
                break;
            }
        }
    }

    // Gripper is within the margin sideways and above (or barely below) the grab point.
    bool InCatchZone(out float horiz, out float vert)
    {
        Vector3 off = gripPoint.position - topPos;
        vert = Vector3.Dot(off, Vector3.up);
        horiz = new Vector3(off.x, 0f, off.z).magnitude;
        float m = Margin();
        return horiz <= m && vert >= -m * belowTolerance && vert <= m * 2f;
    }

    float Margin()
    {
        if (state == State.Title || state == State.Trial) return catchMargin * trialMarginMultiplier;
        return catchMargin * Mathf.Pow(marginScalePerRound, Mathf.Min(round, Mathf.Max(0, rounds - 1)));
    }

    // ================= helpers =================

    // Tries random joint angles and returns a grip position that is on screen.
    Vector3 SampleReachablePoint(Vector3 avoid, float minDist)
    {
        for (int i = 0; i < 400; i++)
        {
            float y = Mathf.Lerp(Mathf.Max(arm.yawJoint.min, -170f), Mathf.Min(arm.yawJoint.max, 170f), Random.value);
            float p = Mathf.Lerp(arm.pitchJoint.min, arm.pitchJoint.max, Random.Range(0.1f, 0.9f));
            float w = Mathf.Lerp(arm.wristJoint.min, arm.wristJoint.max, Random.Range(0.2f, 0.8f));
            Vector3 g = arm.GripPositionAt(gripPoint, y, p, w);
            Vector3 body = g - uprightRot * grabLocal;   // satellite pivot if its grab point sat here

            if (!InView(g, 0.18f, 0.1f) || !InView(body, 0.1f)) continue;
            if (Vector3.Distance(g, avoid) < minDist) continue;
            return g;
        }
        Debug.LogWarning("SatelliteGame: couldn't find a reachable spot on screen. Move the camera so more of the arm's reach is visible.");
        return gripPoint.position;
    }

    bool InView(Vector3 p, float pad, float extraTop = 0f)
    {
        Vector3 v = cam.WorldToViewportPoint(p);
        return v.z > cam.nearClipPlane && v.x > pad && v.x < 1f - pad && v.y > pad && v.y < 1f - pad - extraTop;
    }

    Quaternion SatRot() => Quaternion.AngleAxis(spin, Vector3.up) * uprightRot;

    void PoseSatellite(Vector3 grabWorld)
    {
        satellite.rotation = SatRot();
        satellite.position = grabWorld - satellite.rotation * grabLocal;
    }

    void ShowSatellite(bool on)
    {
        satVisible = on;
        satellite.gameObject.SetActive(on);
    }

    void Flash(string msg) { 
        message = msg; 
        messageUntil = Time.time + 3f; 
    }

    static bool Pressed(string key)
    {
#if ENABLE_INPUT_SYSTEM
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null) return false;
        switch (key)
        {
            case "space": return kb.spaceKey.wasPressedThisFrame;
            case "c": return kb.cKey.wasPressedThisFrame;
            case "r": return kb.rKey.wasPressedThisFrame;
            case "skip": return kb.shiftKey.isPressed && kb.sKey.wasPressedThisFrame;
        }
        return false;
#else
        return Input.GetKeyDown(key);
#endif
    }

    // ================= visuals =================

    void LateUpdate()   // after ArmTest has moved the joints this frame
    {
        if (held) PoseSatellite(gripPoint.position);

        float m = Margin();
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 4f);
        bool showTrial = state == State.Trial;
        bool showRound = state == State.Round && satVisible && respawnIn <= 0f;

        ghost.SetActive(showTrial);
        ringFlat.enabled = showTrial || showRound;
        ringFacing.enabled = showTrial;
        approachLine.enabled = showRound;
        gripRing.enabled = state == State.Title || state == State.Setup || state == State.Trial || state == State.Round;

        if (showTrial)
        {
            bool inside = Vector3.Distance(gripPoint.position, trialTarget) <= m;
            Color c = inside ? readyColor : outlineColor;
            ghost.transform.rotation = uprightRot;
            ghost.transform.position = trialTarget - uprightRot * grabLocal;
            SetGhostColor(new Color(c.r, c.g, c.b, 0.2f + 0.2f * pulse));
            DrawRing(ringFlat, trialTarget, Vector3.up, m, c);
            DrawRing(ringFacing, trialTarget, -cam.transform.forward, m, c);
        }

        if (showRound)
        {
            bool ok = InCatchZone(out _, out _);
            Color c = ok ? readyColor : new Color(outlineColor.r, outlineColor.g, outlineColor.b, 0.5f + 0.5f * pulse);
            DrawRing(ringFlat, topPos, Vector3.up, m, c);
            approachLine.SetPosition(0, topPos);
            approachLine.SetPosition(1, topPos + Vector3.up * m * 2f);
            approachLine.startColor = c;
            approachLine.endColor = new Color(c.r, c.g, c.b, 0f);
        }

        if (gripRing.enabled) DrawRing(gripRing, gripPoint.position, -cam.transform.forward, m * 0.2f, Color.white);
    }

    void DrawRing(LineRenderer lr, Vector3 center, Vector3 normal, float radius, Color col)
    {
        Vector3 a = Vector3.Cross(normal, Vector3.up);
        if (a.sqrMagnitude < 1e-4f) a = Vector3.Cross(normal, Vector3.right);
        a.Normalize();
        Vector3 b = Vector3.Cross(normal, a).normalized;
        int n = lr.positionCount;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)n * Mathf.PI * 2f;
            lr.SetPosition(i, center + (a * Mathf.Cos(t) + b * Mathf.Sin(t)) * radius);
        }
        lr.startColor = lr.endColor = col;
    }

    void SetGhostColor(Color c)
    {
        if (ghostMat.HasProperty("_BaseColor")) ghostMat.SetColor("_BaseColor", c);
        if (ghostMat.HasProperty("_Color")) ghostMat.SetColor("_Color", c);
    }

    // ================= UI =================

#if UNITY_EDITOR
    void OnValidate()
    {
        if (!titleFont) titleFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(GameFonts.TitlePath);
        if (!bodyFont || GameFonts.IsOldFont(bodyFont)) bodyFont = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(GameFonts.BodyPath);
        if (!controllerQr) controllerQr = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/ArmGame/ControllerQR.png");
        big = null;   // rebuild styles if fonts change in Play mode
    }
#endif

    void MakeStyles()
    {
        styleHeight = Screen.height;
        float s = Screen.height / 1080f;
        // Shared fonts: Astro Space for titles, Clean Typewriter for the rest, kept sharp (see GameFonts).
        GameFonts.Title = titleFont;
        GameFonts.Body = bodyFont;
        titleFont = GameFonts.Title;
        bodyFont = GameFonts.Body;
        big = new GUIStyle(GUI.skin.label) { font = titleFont, fontSize = Mathf.RoundToInt(64 * s), alignment = TextAnchor.MiddleCenter, fontStyle = titleFont ? FontStyle.Normal : FontStyle.Bold, richText = true };
        mid = new GUIStyle(GUI.skin.label) { font = bodyFont, fontSize = Mathf.RoundToInt(30 * s), alignment = TextAnchor.MiddleCenter, wordWrap = true, richText = true };
        small = new GUIStyle(GUI.skin.label) { font = bodyFont, fontSize = Mathf.RoundToInt(24 * s), alignment = TextAnchor.UpperLeft, wordWrap = true, richText = true };
        smallRight = new GUIStyle(small) { alignment = TextAnchor.UpperRight };
        instructions = new GUIStyle(small) { fontSize = Mathf.RoundToInt(34 * s) };
        button = new GUIStyle(GUI.skin.button) { font = bodyFont, fontSize = Mathf.RoundToInt(26 * s) };
        fitted = new GUIStyle(instructions);
        if (panelTex == null)
        {
            panelTex = new Texture2D(1, 1);
            panelTex.SetPixel(0, 0, Color.white);
            panelTex.Apply();
        }
    }

    // Dark backing behind text so the arm and satellites don't make it hard to read.
    GUIStyle fitted;
    Texture2D panelTex;
    static readonly Color PanelColor = new Color(0.03f, 0.04f, 0.06f, 0.93f);

    void Backdrop(Rect r, float alpha = 0.93f, bool outline = true)
    {
        if (Event.current.type != EventType.Repaint) return;
        Color old = GUI.color;
        GUI.color = new Color(PanelColor.r, PanelColor.g, PanelColor.b, alpha);
        GUI.DrawTexture(r, panelTex);
        if (outline)
        {
            GUI.color = new Color(outlineColor.r, outlineColor.g, outlineColor.b, 0.45f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 2f), panelTex);
            GUI.DrawTexture(new Rect(r.x, r.yMax - 2f, r.width, 2f), panelTex);
            GUI.DrawTexture(new Rect(r.x, r.y, 2f, r.height), panelTex);
            GUI.DrawTexture(new Rect(r.xMax - 2f, r.y, 2f, r.height), panelTex);
        }
        GUI.color = old;
    }

    // A panel that fits on screen whatever its size or shape, centred.
    static Rect FitPanel(float wantW, float wantH, float W, float H)
    {
        float m = 16f;
        float w = Mathf.Min(wantW, W - 2f * m), h = Mathf.Min(wantH, H - 2f * m);
        return new Rect(Mathf.Round((W - w) / 2f), Mathf.Round((H - h) / 2f), Mathf.Round(w), Mathf.Round(h));
    }

    // Shrinks the instruction text (down to a readable minimum) until it fits the space available.
    GUIStyle FitText(string text, float width, float height, float s)
    {
        int size = Mathf.RoundToInt(34 * s);
        int min = Mathf.Max(13, Mathf.RoundToInt(18 * s));
        fitted.fontSize = size;
        while (size > min && fitted.CalcHeight(new GUIContent(text), width) > height)
            fitted.fontSize = --size;
        return fitted;
    }

    void OnGUI()
    {
        if (big == null || styleHeight != Screen.height) MakeStyles();
        float s = Screen.height / 1080f, W = Screen.width, H = Screen.height;

        // Top-right: phone status
        string phone = arm.PhoneConnected ? "<color=#7f7>Phone connected</color>" : $"<color=#fa6>{arm.Status}</color>";
        GUI.Label(new Rect(W - 720 * s, 20 * s, 700 * s, 40 * s), phone, smallRight);

        switch (state)
        {
            case State.Title: DrawTitle(s, W, H); break;
            case State.Setup: DrawSetup(s, W, H); break;

            case State.Trial:
            {
                DrawHeader(s, W, "TRIAL", "Line the satellite up with the glowing outline, then click to release");
                break;
            }

            case State.TrialDone:
                DrawHeader(s, W, "TRIAL COMPLETE", "Real rounds are next. Grab each satellite from above its glowing ring");
                break;

            case State.Countdown:
            {
                int n = Mathf.CeilToInt(countdownSeconds - stateTime);
                Backdrop(new Rect(Mathf.Round(W * 0.3f), Mathf.Round(H * 0.3f - 10 * s), Mathf.Round(W * 0.4f), Mathf.Round(270 * s)), 0.8f);
                GUI.Label(new Rect(0, H * 0.3f, W, 100 * s), $"ROUND {round + 1} / {rounds}", big);
                GUI.Label(new Rect(0, H * 0.3f + 110 * s, W, 140 * s), $"<size={Mathf.RoundToInt(110 * s)}>{Mathf.Max(n, 1)}</size>", big);
                break;
            }

            case State.Round:
            {
                // Timer stays hidden while playing; the time is shown once the satellite is caught.
                DrawHeader(s, W, $"ROUND {round + 1} / {rounds}",
                    timing ? "Get the gripper ABOVE the glowing ring and click to grab" : "Incoming satellite...");
                break;
            }

            case State.Caught:
                DrawHeader(s, W, $"CAUGHT!  {times[times.Count - 1]:0.00} s", round + 1 < rounds ? "Next satellite incoming..." : "Final satellite secured!");
                break;

            case State.Results: DrawResults(s, W, H); break;
        }

        if (Time.time < messageUntil && state != State.Title && state != State.Results)
        {
            Backdrop(new Rect(Mathf.Round(W * 0.2f), Mathf.Round(H - 175 * s), Mathf.Round(W * 0.6f), Mathf.Round(70 * s)), 0.8f, false);
            GUI.Label(new Rect(0, H - 170 * s, W, 60 * s), message, mid);
        }

        if (state == State.Trial || state == State.Round)
        {
            Backdrop(new Rect(Mathf.Round(10 * s), Mathf.Round(H - 66 * s), Mathf.Round(330 * s), Mathf.Round(50 * s)), 0.75f, false);
            GUI.Label(new Rect(20 * s, H - 60 * s, W, 40 * s),
                "Click: release & grab", small);
        }
    }

    void DrawHeader(float s, float W, string title, string sub)
    {
        Backdrop(new Rect(Mathf.Round(W * 0.12f), Mathf.Round(12 * s), Mathf.Round(W * 0.76f), Mathf.Round(180 * s)), 0.8f);
        GUI.Label(new Rect(0, 20 * s, W, 90 * s), title, big);
        GUI.Label(new Rect(W * 0.15f, 110 * s, W * 0.7f, 70 * s), sub, mid);
    }

    void DrawTitle(float s, float W, float H)
    {
        Rect r = FitPanel(1400 * s, 900 * s, W, H);
        Backdrop(r);
        float pad = Mathf.Round(Mathf.Max(14f, 36 * s));
        float innerW = r.width - 2f * pad;

        // The Start button is pinned to the bottom of the panel, so text can never push it off screen.
        float buttonH = Mathf.Round(Mathf.Max(40f, 70 * s));
        Rect btn = new Rect(r.x + pad, r.yMax - pad - buttonH, innerW, buttonH);

        string body =
            "<b>Controls</b>\n" +
            "• Phone: turn left/right and tilt up/down to aim the arm\n" +
            "• Mouse up/down: bend the wrist\n" +
            "• Mouse click: release or grab\n\n" +
            "<b>How to play</b>\n" +
            "1. Trial: Release the satellite inside the glowing outline.\n" +
            $"2. {rounds} rounds: a satellite drifts past. Get the gripper ABOVE its glowing ring and grab it.\n" +
            "3. If you miss a satellite don't worry, another will come by.\n\n" +
            "Next you'll connect your phone as the controller.";

        float titleH = big.CalcHeight(new GUIContent("SATELLITE CATCH"), innerW);
        Rect titleRect = new Rect(r.x + pad, r.y + pad, innerW, titleH);
        float textTop = titleRect.yMax + Mathf.Round(10 * s);
        float textH = btn.y - Mathf.Round(12 * s) - textTop;

        GUI.Label(titleRect, "SATELLITE CATCH", big);
        GUIStyle textStyle = FitText(body, innerW, textH, s);
        GUI.Label(new Rect(r.x + pad, textTop, innerW, textH), body, textStyle);
        if (GUI.Button(btn, "Next: connect your phone (Space)", button)) EnterSetup();
    }

    // Step-by-step phone connection. Each step ticks green on its own; Start unlocks when all are done.
    void DrawSetup(float s, float W, float H)
    {
        Rect r = FitPanel(1400 * s, 900 * s, W, H);
        Backdrop(r);
        float pad = Mathf.Round(Mathf.Max(14f, 36 * s));
        float innerW = r.width - 2f * pad;
        float buttonH = Mathf.Round(Mathf.Max(40f, 70 * s));
        Rect btn = new Rect(r.x + pad, r.yMax - pad - buttonH, innerW, buttonH);

        string url = arm.ControllerUrl;
        bool showQr = controllerQr != null && string.Equals(url, controllerQrUrl, System.StringComparison.OrdinalIgnoreCase);

        float titleH = big.CalcHeight(new GUIContent("CONNECT YOUR PHONE"), innerW);
        GUI.Label(new Rect(r.x + pad, r.y + pad, innerW, titleH), "CONNECT YOUR PHONE", big);
        float top = r.y + pad + titleH + Mathf.Round(10 * s);
        float bottom = btn.y - Mathf.Round(12 * s);

        // QR code on the right (if there's room), steps on the left.
        float qrSize = showQr ? Mathf.Round(Mathf.Min(innerW * 0.34f, bottom - top, 420 * s)) : 0f;
        if (qrSize < 90f) { showQr = false; qrSize = 0f; }
        float stepsW = innerW - (showQr ? qrSize + pad : 0f);
        if (showQr)
        {
            Rect q = new Rect(r.xMax - pad - qrSize, top, qrSize, qrSize);
            if (Event.current.type == EventType.Repaint)
            {
                controllerQr.filterMode = FilterMode.Point;   // keep the squares sharp
                GUI.DrawTexture(q, controllerQr, ScaleMode.ScaleToFit);
            }
            GUI.Label(new Rect(q.x, q.yMax + 4f, q.width, 30f * Mathf.Max(s, 0.6f)), "Scan with your phone camera", small);
        }

        bool relay = arm.RelayConnected, phone = arm.PhoneConnected, calibrated = setupCalibrated;
        string Tick(bool done) => done ? "<color=#5f8>[DONE]</color>" : "<color=#fa6>[ .. ]</color>";

        string steps =
            $"{Tick(phone)}  <b>1. Open the controller on your phone</b>\n" +
            (showQr ? "Scan the QR code, or go to:\n" : "On your phone's browser, go to:\n") +
            $"<color=#8df>{url}</color>\n\n" +
            $"{Tick(phone)}  <b>2. Tap Start on the phone</b>\n" +
            "If it asks for motion access, tap Allow.\n" +
            (phone ? "<color=#5f8>Phone connected!</color>\n\n"
                   : relay ? "<color=#fa6>Waiting for the phone...</color>\n\n"
                           : $"<color=#fa6>Game can't reach the server yet: {arm.Status}</color>\n\n") +
            $"{Tick(calibrated)}  <b>3. Calibrate</b>\n" +
            "Hold the phone flat, pointing straight at this screen, then tap Calibrate on the phone.\n" +
            (calibrated ? "<color=#5f8>Calibrated! Move the phone: the arm should follow.</color>"
                        : phone ? "<color=#fa6>Waiting for Calibrate...</color>" : "");

        GUIStyle textStyle = FitText(steps, stepsW, bottom - top, s);
        GUI.Label(new Rect(r.x + pad, top, stepsW, bottom - top), steps, textStyle);

        bool ready = SetupReady;
        GUI.enabled = ready;
        if (GUI.Button(btn, ready ? "Start trial (Space)" : "Finish the steps above to start", button)) BeginTrial();
        GUI.enabled = true;
    }

    void DrawResults(float s, float W, float H)
    {
        Rect r = FitPanel(900 * s, 640 * s, W, H);
        Backdrop(r);
        float buttonH = Mathf.Round(Mathf.Max(40f, 60 * s)), pad = Mathf.Round(Mathf.Max(14f, 36 * s));
        Rect btn = new Rect(r.x + pad, r.yMax - pad - buttonH, r.width - 2f * pad, buttonH);
        if (GUI.Button(btn, "Play again (Space)", button)) EnterTitle();
        GUILayout.BeginArea(new Rect(r.x + pad, r.y + pad, r.width - 2f * pad, btn.y - r.y - pad - 8f));
        GUILayout.Label("MISSION COMPLETE", big);
        GUILayout.Space(20 * s);

        float total = 0f, best = float.MaxValue;
        int bestIndex = 0;
        for (int i = 0; i < times.Count; i++)
        {
            total += times[i];
            if (times[i] < best) { best = times[i]; bestIndex = i; }
        }
        for (int i = 0; i < times.Count; i++)
        {
            string star = i == bestIndex ? "  <color=#ff7>BEST</color>" : "";
            GUILayout.Label($"Round {i + 1}:   <b>{times[i]:0.00} s</b>   " +
                            $"<color=#aaa>({missedGrabs[i]} missed grabs, {gotAway[i]} got away)</color>{star}", mid);
        }
        GUILayout.Space(20 * s);
        GUILayout.Label($"<b>Total: {total:0.00} s</b>", mid);
        GUILayout.EndArea();
    }
}
