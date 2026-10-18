using System;
using UnityEngine;
using NativeWebSocket;

// Phone + mouse control for the robotic arm.
//   Phone yaw   (turn left/right) -> Joint 1: Segment1 -> Segment2 (local Y)
//   Phone pitch (tilt up/down)    -> Joint 2: Segment2 -> Segment3 (local X)
//   Mouse up/down                 -> Joint 3: Segment3 -> Segment4 (local X)
//   Left click / phone Grab button -> grip (SatelliteGame listens to GripPressed)
// Requires the NativeWebSocket package:
//   Package Manager > + > Add package from git URL >
//   https://github.com/endel/NativeWebSocket.git#upm
public class ArmTest : MonoBehaviour
{
    [Serializable]
    class PhoneMessage { public float[] q; public bool snare; public bool cal; }

    [Serializable]
    public class Joint
    {
        public Transform segment;
        public Vector3 axis = Vector3.up;   // local hinge axis
        public bool invert;                  // flip direction if it turns the wrong way
        public float min = -180f, max = 180f;

        Quaternion rest;
        bool hasRest;

        public void Init()
        {
            if (segment != null && !hasRest) { rest = segment.localRotation; hasRest = true; }
        }

        public void Apply(float angle)
        {
            if (segment == null) return;
            Init();
            segment.localRotation = rest * Quaternion.AngleAxis(angle, axis);
        }
    }

    [Header("Player arm")]
    public Joint yawJoint   = new Joint { axis = Vector3.up };                             // Segment2
    public Joint pitchJoint = new Joint { axis = Vector3.right, min = -80f, max = 80f };   // Segment3
    public Joint wristJoint = new Joint { axis = Vector3.right, min = -90f, max = 90f };   // Segment4

    [Header("Mouse wrist control")]
    [Tooltip("Bottom of the screen = Min angle, top = Max angle")]
    [Range(1f, 30f)] public float wristSmoothing = 15f;
    public bool gripClosed;   // shown state; SatelliteGame sets this while holding

    [Header("Connection")]
    [Tooltip("Local: ws://localhost:8080/?role=game   Cloud: wss://YOUR-DOMAIN/?role=game")]
    public string serverUrl = "ws://localhost:8080/?role=game";

    [Header("Feel")]
    [Range(1f, 30f)] public float smoothing = 12f;

    [Header("Axis tuning (change live in Play mode)")]
    [Tooltip("0 = phone axes as-is, 1 = flat-phone ENU to Unity Y-up")]
    public int mapping = 1;
    public bool worldSpaceDelta = true;
    public bool invertX, invertY, invertZ;

    [Header("Debug")]
    [Tooltip("Old test overlay. SatelliteGame turns this off and draws its own UI.")]
    public bool showDebugGui = true;

    // Fired on left click or the phone's Grab button. Argument = mouse position
    // (screen pixels), or (-1, -1) when it came from the phone.
    public event Action<Vector2> GripPressed;

    public string Status => info;
    public bool PhoneConnected => hasData;

    WebSocket ws;
    float[] lastQ;
    Quaternion reference = Quaternion.identity;
    Quaternion smoothed = Quaternion.identity;
    bool hasData;
    bool needsCalibrate = true;
    string info = "Connecting to relay...";
    string result = "";

    float yaw, pitch, wrist;   // current joint angles

    void Awake()
    {
        yawJoint.Init();
        pitchJoint.Init();
        wristJoint.Init();
        wrist = Mathf.Clamp(0f, wristJoint.min, wristJoint.max);
    }

    bool quitting;

    void Start()
    {
        ConnectLoop();
    }

    // Connects to the relay and reconnects automatically if the link drops
    // (wifi hiccup, server restart, etc.).
    async void ConnectLoop()
    {
        while (!quitting)
        {
            ws = new WebSocket(serverUrl);
            ws.OnOpen += () => info = hasData ? "Phone connected" : "Relay connected. Waiting for phone...";
            ws.OnError += e => info = "Relay error: " + e;
            ws.OnClose += c => { if (!quitting) info = "Relay disconnected (" + c + "), retrying..."; };
            ws.OnMessage += bytes =>
            {
                var msg = JsonUtility.FromJson<PhoneMessage>(System.Text.Encoding.UTF8.GetString(bytes));
                if (msg.q != null && msg.q.Length == 4) { lastQ = msg.q; hasData = true; info = "Phone connected"; }
                if (msg.cal) Calibrate();
                if (msg.snare) PressGrip(new Vector2(-1f, -1f));
            };

            try { await ws.Connect(); }   // returns once the connection closes
            catch (Exception e) { info = "Relay error: " + e.Message; }

            if (quitting || this == null) break;
            await System.Threading.Tasks.Task.Delay(2000);
        }
    }

    void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        ws?.DispatchMessageQueue();
#endif
        UpdateWrist();   // mouse works even before the phone connects

        if (!hasData) return;
        if (needsCalibrate) { Calibrate(); needsCalibrate = false; }

        // Phone rotation relative to the calibrated pose
        Quaternion current = Convert(lastQ);
        Quaternion delta = worldSpaceDelta
            ? current * Quaternion.Inverse(reference)
            : Quaternion.Inverse(reference) * current;

        delta = new Quaternion(
            invertX ? -delta.x : delta.x,
            invertY ? -delta.y : delta.y,
            invertZ ? -delta.z : delta.z,
            delta.w);

        float t = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
        smoothed = Quaternion.Slerp(smoothed, delta, t);

        // Split the phone's pointing direction into yaw + pitch (roll is ignored).
        Vector3 f = smoothed * Vector3.forward;
        float rawYaw   = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        float rawPitch = -Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;

        if (yawJoint.invert)   rawYaw = -rawYaw;
        if (pitchJoint.invert) rawPitch = -rawPitch;

        yaw   = Mathf.Clamp(rawYaw,   yawJoint.min,   yawJoint.max);
        pitch = Mathf.Clamp(rawPitch, pitchJoint.min, pitchJoint.max);

        yawJoint.Apply(yaw);
        pitchJoint.Apply(pitch);
    }

    void UpdateWrist()
    {
        Vector2 mouse;
        bool clicked;
#if ENABLE_INPUT_SYSTEM
        var m = UnityEngine.InputSystem.Mouse.current;
        if (m == null) return;
        mouse = m.position.ReadValue();
        clicked = m.leftButton.wasPressedThisFrame;
#else
        mouse = Input.mousePosition;
        clicked = Input.GetMouseButtonDown(0);
#endif
        // Mouse height on screen -> wrist angle
        float t01 = Mathf.Clamp01(mouse.y / Screen.height);
        if (wristJoint.invert) t01 = 1f - t01;
        float goal = Mathf.Lerp(wristJoint.min, wristJoint.max, t01);
        wrist = Mathf.Lerp(wrist, goal, 1f - Mathf.Exp(-wristSmoothing * Time.deltaTime));
        wristJoint.Apply(wrist);

        if (clicked && !(showDebugGui && OverDebugGui(mouse))) PressGrip(mouse);
    }

    // Ignore clicks on the debug buttons (top-left panel)
    static bool OverDebugGui(Vector2 mouse) => mouse.x < 700f && (Screen.height - mouse.y) < 200f;

    void PressGrip(Vector2 mouse)
    {
        if (GripPressed != null) { GripPressed(mouse); return; }
        gripClosed = !gripClosed;
        result = gripClosed ? "Gripper CLOSED" : "Gripper OPEN";
    }

    // Where the grip point would be with these joint angles (the arm is put back afterwards).
    // Used by SatelliteGame to pick spots the arm can actually reach.
    public Vector3 GripPositionAt(Transform grip, float yawAngle, float pitchAngle, float wristAngle)
    {
        Quaternion a = yawJoint.segment ? yawJoint.segment.localRotation : Quaternion.identity;
        Quaternion b = pitchJoint.segment ? pitchJoint.segment.localRotation : Quaternion.identity;
        Quaternion c = wristJoint.segment ? wristJoint.segment.localRotation : Quaternion.identity;

        yawJoint.Apply(yawAngle);
        pitchJoint.Apply(pitchAngle);
        wristJoint.Apply(wristAngle);
        Vector3 p = grip.position;

        if (yawJoint.segment) yawJoint.segment.localRotation = a;
        if (pitchJoint.segment) pitchJoint.segment.localRotation = b;
        if (wristJoint.segment) wristJoint.segment.localRotation = c;
        return p;
    }

    Quaternion Convert(float[] q)
    {
        float x = q[0], y = q[1], z = q[2], w = q[3];
        switch (mapping)
        {
            case 0: return new Quaternion(-x, -y, z, w);   // flip handedness only
            default: return new Quaternion(-x, -z, -y, w); // swap Y/Z (Z-up -> Y-up) + handedness
        }
    }

    public void Calibrate()
    {
        if (lastQ == null) return;
        reference = Convert(lastQ);
        smoothed = Quaternion.identity;
        result = "Calibrated";
    }

    void OnGUI()
    {
        if (!showDebugGui) return;
        var style = GameFonts.AsBody(new GUIStyle(GUI.skin.label) { fontSize = 22 });
        GUI.Label(new Rect(20, 20, 900, 40), info, style);
        GUI.Label(new Rect(20, 60, 900, 40), result, style);
        GUI.Label(new Rect(20, 100, 900, 40),
            $"Yaw {yaw:0}° / Pitch {pitch:0}° / Wrist {wrist:0}°  |  Grip {(gripClosed ? "CLOSED" : "OPEN")}  |  Mapping {mapping}", style);

        if (GUI.Button(new Rect(20, 150, 160, 40), "Calibrate")) Calibrate();
        if (GUI.Button(new Rect(190, 150, 160, 40), "Next mapping")) { mapping = (mapping + 1) % 2; Calibrate(); }
    }

    void OnDestroy()
    {
        quitting = true;
        ws?.Close();
    }

    async void OnApplicationQuit()
    {
        quitting = true;
        if (ws != null) await ws.Close();
    }
}
