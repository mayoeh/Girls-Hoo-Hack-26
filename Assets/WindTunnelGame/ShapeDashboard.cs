using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;

// Two panels: TOOLS (top right: mirror, dots, undo) and NOSE SHAPE (bottom: the shape numbers, editable).
// Sliders and number boxes change the shape, and every change is undoable.
// Angle and bluntness snap the nose to a clean rounded cone; length and tip height keep your sculpted shape.
public class ShapeDashboard : MonoBehaviour
{
    public RocketModel rocket;
    public NoseEditor editor;
    public ShapeHistory history;
    public float toolsWidth = 270f;     // top-right tools panel
    public float shapeWidth = 940f;     // bottom shape panel (shrinks on narrow screens)

    enum Field { None, Angle, Bluntness, Length, TipHeight }

    ShapeMetrics m;       // live values
    ShapeMetrics view;    // values the panel is drawn from this frame (kept steady so the layout can't change mid-draw)
    Field sliding = Field.None;
    float slideValue;
    float holdAngle, holdBluntness;   // the other cone value, held fixed while one changes
    string fitNote = "";
    string editingName, editingText;

    GUIStyle panel, head, label, value, note, warn, field, button;

    void Update()
    {
        if (rocket == null) return;
        m = ShapeMetrics.Measure(rocket);

        // A whole slider drag is one undo step: it ends when the mouse is released.
        if (sliding != Field.None && (Mouse.current == null || !Mouse.current.leftButton.isPressed))
        {
            sliding = Field.None;
            EndEdit();
        }
    }

    // ---------- applying edits ----------

    void BeginEdit()
    {
        if (history != null) history.BeginChange();
        holdAngle = m.Angle;
        holdBluntness = m.Bluntness;
    }

    void EndEdit()
    {
        if (history != null) history.EndChange();
    }

    void Apply(Field f, float v)
    {
        switch (f)
        {
            case Field.Angle:     ApplyCone(v, holdBluntness); break;
            case Field.Bluntness: ApplyCone(holdAngle, v); break;
            case Field.Length:    rocket.shape.length = v; rocket.Rebuild(); break;
            case Field.TipHeight: rocket.shape.tipHeight = v; rocket.Rebuild(); break;
        }
        m = ShapeMetrics.Measure(rocket);
    }

    void ApplyCone(float angle, float bluntness)
    {
        var s = FitCone(angle, bluntness, out bool limited, out ShapeMetrics got);
        s.tipHeight = rocket.shape.tipHeight;
        rocket.shape = s;
        rocket.Rebuild();

        bool missed = Mathf.Abs(got.Angle - angle) > 1f || Mathf.Abs(got.Bluntness - bluntness) > 0.02f;
        fitNote = limited ? "That angle needs a nose longer than 5 × radius, so this is as sharp as the length allows."
                : missed  ? $"Closest clean cone: {got.Angle:0}° at bluntness {got.Bluntness:0.00}."
                : "";
    }

    // Build a cone, measure it the way the readout does, adjust, repeat. Keeps the closest result.
    NoseShape FitCone(float angle, float bluntness, out bool limited, out ShapeMetrics got)
    {
        int n = rocket.shape.points.Count;
        float a = angle, b = bluntness, bestError = float.MaxValue;
        NoseShape best = null;
        got = default;
        limited = false;

        for (int i = 0; i < 8; i++)
        {
            var s = NoseShape.SphereCone(a, b, n, out bool lim);
            var sm = rocket.MeasureShape(s);
            float ea = angle - sm.Angle, eb = bluntness - sm.Bluntness;
            float error = Mathf.Abs(ea) / 30f + Mathf.Abs(eb);
            if (error < bestError) { bestError = error; best = s; got = sm; limited = lim; }
            if (Mathf.Abs(ea) < 0.3f && Mathf.Abs(eb) < 0.005f) break;
            a = Mathf.Clamp(a + ea, 1f, 170f);
            b = Mathf.Clamp01(b + eb);
        }
        return best;
    }

    // ---------- panel ----------

    void OnGUI()
    {
        if (rocket == null) return;
        EnsureStyles();
        Event e = Event.current;
        if (e.type == EventType.Layout) view = m;

        // Clicking anywhere ends typing; the number is committed.
        if (e.type == EventType.MouseDown && editingName != null) GUI.FocusControl(null);

        DrawToolsPanel(e);
        DrawShapePanel(e);

        UIPanels.Typing = editingName != null;
    }

    void OnDisable()
    {
        UIPanels.Clear(this);
        UIPanels.Typing = false;
        editingName = null;
    }

    // Top right: editing tools.
    void DrawToolsPanel(Event e)
    {
        Vector2 origin = new(Screen.width - toolsWidth - 12f, 12f);
        GUILayout.BeginArea(new Rect(origin.x, origin.y, toolsWidth, Screen.height - 24f));
        GUILayout.BeginVertical(panel);

        if (UIPanels.Header("tools", "TOOLS", head))
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(view.mirrored ? "Mirror: ON" : "Mirror: OFF", button) && editor != null) editor.ToggleMirror();
            GUILayout.FlexibleSpace();
            GUILayout.Label($"Dots {rocket.shape.points.Count}", label);
            if (GUILayout.Button("-", button, GUILayout.Width(28f)) && editor != null) editor.ChangePointCount(-1);
            if (GUILayout.Button("+", button, GUILayout.Width(28f)) && editor != null) editor.ChangePointCount(+1);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool hasHistory = history != null;
            GUI.enabled = hasHistory && history.UndoCount > 0;
            if (GUILayout.Button($"Undo ({(hasHistory ? history.UndoCount : 0)})", button)) history.Undo();
            GUI.enabled = hasHistory && history.RedoCount > 0;
            if (GUILayout.Button("Redo", button)) history.Redo();
            GUI.enabled = hasHistory;
            if (GUILayout.Button("Reset", button)) history.ResetToCleanBase();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        GUILayout.EndVertical();
        RegisterRect(e, origin, 0);
        GUILayout.EndArea();
    }

    // Bottom centre: the shape's numbers. The four on the left are editable.
    void DrawShapePanel(Event e)
    {
        float w = Mathf.Min(Screen.width - 24f, shapeWidth);
        const float areaHeight = 200f;
        Vector2 origin = new((Screen.width - w) / 2f, Screen.height - areaHeight - 40f);   // leaves room for the help line

        GUILayout.BeginArea(new Rect(origin.x, origin.y, w, areaHeight));
        GUILayout.FlexibleSpace();                       // sits on the bottom edge, also when collapsed
        GUILayout.BeginVertical(panel);

        if (UIPanels.Header("shape", "NOSE SHAPE", head))
        {
            const float infoWidth = 190f;
            float col = Mathf.Max(200f, (w - 28f - infoWidth - 40f) / 2f);

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(col));
            Row("Angle (°)",  Field.Angle,     view.Angle,     20f, 140f, "0");
            Row("Bluntness",  Field.Bluntness, view.Bluntness, 0f,  1f,   "0.00");
            GUILayout.EndVertical();
            GUILayout.Space(20f);
            GUILayout.BeginVertical(GUILayout.Width(col));
            Row("Length",     Field.Length,    view.noseLength,        NoseShape.MinLength, NoseShape.MaxLength, "0.00");
            Row("Tip height", Field.TipHeight, rocket.shape.tipHeight, -1f, 1f, "0.00");
            GUILayout.EndVertical();
            GUILayout.Space(20f);
            GUILayout.BeginVertical(GUILayout.Width(infoWidth));
            Info("Fineness", $"{view.fineness:0.00}");
            Info("Fullness", $"{view.fullness * 100f:0}%");
            Info("Tip bend", $"{view.tipBend:+0;-0;0}°");
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            bool warning = fitNote.Length > 0 || !view.InTestedRange;
            GUILayout.Label(NoteText(), warning ? warn : note);
        }

        GUILayout.EndVertical();
        RegisterRect(e, origin, 1);
        GUILayout.EndArea();
    }

    string NoteText()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (fitNote.Length > 0) parts.Add(fitNote);
        if (!view.InTestedRange) parts.Add("Angle is outside the 10–60° Mary Jackson tested.");
        if (!view.mirrored)
            parts.Add($"Top {view.top.noseAngle:0}° / {view.top.bluntness:0.00}  ·  Bottom {view.bottom.noseAngle:0}° / {view.bottom.bluntness:0.00}");
        if (parts.Count == 0)
            parts.Add("Angle and bluntness snap to a clean rounded cone. Length and tip height keep your shape.");
        return string.Join("     ", parts);
    }

    void RegisterRect(Event e, Vector2 origin, int slot)
    {
        if (e.type != EventType.Repaint) return;
        Rect r = GUILayoutUtility.GetLastRect();
        r.position += origin;
        UIPanels.Set(this, r, slot);
    }

    // Slider + number box. Dragging the slider updates live; typing commits on Enter or click-away.
    void Row(string name, Field f, float current, float min, float max, string format)
    {
        float shown = Mathf.Clamp(sliding == f ? slideValue : current, min, max);

        GUILayout.BeginHorizontal();
        GUILayout.Label(name, label, GUILayout.Width(84f));
        float v = GUILayout.HorizontalSlider(shown, min, max, GUILayout.MinWidth(80f));
        float? typed = NumberBox(name, shown, format);
        GUILayout.EndHorizontal();

        if (v != shown)
        {
            if (sliding != f)
            {
                if (sliding != Field.None) EndEdit();
                BeginEdit();
                sliding = f;
            }
            slideValue = v;
            Apply(f, v);
        }

        if (typed.HasValue)
        {
            BeginEdit();
            Apply(f, Mathf.Clamp(typed.Value, min, max));
            EndEdit();
        }
    }

    float? NumberBox(string name, float current, string format)
    {
        Event e = Event.current;
        bool focused = GUI.GetNameOfFocusedControl() == name;
        bool wasEditing = editingName == name;
        bool isKey = focused && e.type == EventType.KeyDown;
        bool enter = isKey && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
        bool escape = isKey && e.keyCode == KeyCode.Escape;

        if (focused && !wasEditing)
        {
            editingName = name;
            editingText = current.ToString(format, CultureInfo.InvariantCulture);
            wasEditing = true;
        }

        GUI.SetNextControlName(name);
        string shownText = focused ? editingText : current.ToString(format, CultureInfo.InvariantCulture);
        string text = GUILayout.TextField(shownText, field, GUILayout.Width(52f));
        if (focused) editingText = text;

        if (escape)
        {
            editingName = null;
            GUI.FocusControl(null);
            e.Use();
            return null;
        }

        if (enter || (wasEditing && !focused))
        {
            editingName = null;
            if (enter) { GUI.FocusControl(null); e.Use(); }
            if (float.TryParse(editingText, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                return v;
        }
        return null;
    }

    void Info(string name, string val)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(name, label);
        GUILayout.FlexibleSpace();
        GUILayout.Label(val, value);
        GUILayout.EndHorizontal();
    }

    void EnsureStyles()
    {
        if (panel != null) return;
        var bg = new Texture2D(1, 1);
        bg.SetPixel(0, 0, new Color(0.08f, 0.08f, 0.09f, 0.85f));
        bg.Apply();

        panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(14, 14, 12, 12) };
        panel.normal.background = bg;
        head = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
        head.normal.textColor = new Color(0.6f, 0.75f, 0.85f);
        label = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        label.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
        value = new GUIStyle(label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
        value.normal.textColor = Color.white;
        note = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
        note.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
        warn = new GUIStyle(note);
        warn.normal.textColor = new Color(1f, 0.7f, 0.3f);
        field = new GUIStyle(GUI.skin.textField) { fontSize = 13, alignment = TextAnchor.MiddleRight };
        button = new GUIStyle(GUI.skin.button) { fontSize = 12 };

        ShockwaveFonts.AsTitle(head);
        foreach (var s in new[] { label, value, note, warn, field, button })
            ShockwaveFonts.AsBody(s);
    }
}