using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

// Saved trials: S to save, hover to compare (orange ghost outline), click to load (undoable), × to delete.
// Trials only last for one run of the game: every new session starts with an empty list.
public class TrialLog : MonoBehaviour
{
    public RocketModel rocket;
    public ShapeHistory history;
    [Tooltip("Saving sends the design straight to the wind tunnel. Found automatically if left empty.")]
    public TestBench testBench;
    [Tooltip("Material for the ghost outline. DotMaterial works.")]
    public Material lineMaterial;
    public Color ghostColor = new(1f, 0.6f, 0.2f);
    public float panelWidth = 300f;
    public int maxShown = 8;

    bool saveRequested;

    public readonly List<TrialRecord> trials = new();
    public event System.Action<TrialRecord> TrialSaved;

    const int Samples = 48;
    readonly Vector2[] sTop = new Vector2[Samples], sBottom = new Vector2[Samples];
    readonly Vector3[] linePoints = new Vector3[Samples];

    // Thumbnails share one scale (in body radii), so longer noses look longer.
    const int ThumbCols = 56;
    const float ThumbXMin = -5.2f, ThumbXMax = 1f, ThumbYRange = 2.1f;

    TrialRecord hovered, hoverNext;
    LineRenderer ghostTop, ghostBottom;
    MaterialPropertyBlock mpb;
    GUIStyle panel, head, title, note, button, small;
    Texture2D white;

    static readonly Color ThumbColor = new(0.75f, 0.75f, 0.78f);
    static readonly Color CurrentColor = new(0.3f, 0.82f, 1f);

    void Start()
    {
        if (testBench == null) testBench = FindFirstObjectByType<TestBench>();
        mpb = new MaterialPropertyBlock();
        ghostTop = MakeLine("Ghost top");
        ghostBottom = MakeLine("Ghost bottom");
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && !UIPanels.Typing && kb.sKey.wasPressedThisFrame) SaveAndTest();
        UpdateGhost();
    }

    // ---------- trials ----------

    public TrialRecord SaveTrial(string note = "Saved")
    {
        var m = ShapeMetrics.Measure(rocket);
        int number = 1;
        foreach (var t in trials) number = Mathf.Max(number, t.number + 1);

        var trial = new TrialRecord
        {
            id = System.Guid.NewGuid().ToString(),
            number = number,
            name = $"Trial {number}",
            savedAt = System.DateTime.Now.ToString("o"),
            note = note,
            shape = rocket.shape.Clone(),
            noseAngle = m.Angle,
            bluntness = m.Bluntness,
            fineness = m.fineness,
            fullness = m.fullness,
            tipBend = m.tipBend,
            predictedLow = m.PredictedSmoothFlow(1.6f),
            predictedHigh = m.PredictedSmoothFlow(2.2f),
        };
        trials.Add(trial);
        TrialSaved?.Invoke(trial);
        return trial;
    }

    // Every save goes to the tunnel: the test bench saves the trial and switches to testing.
    public void SaveAndTest()
    {
        if (testBench != null) testBench.SaveAndTest();
        else SaveTrial("Saved");
    }

    // For the wind tunnel: record what actually happened on a run.
    public void AttachResult(TrialRecord t, float mach, float smoothFlow)
    {
        t.hasResult = true;
        t.mach = mach;
        t.smoothFlow = smoothFlow;
    }

    public void LoadTrial(TrialRecord t)
    {
        if (history != null) history.Record(() => rocket.shape = t.shape.Clone());
        else { rocket.shape = t.shape.Clone(); rocket.Rebuild(); }
    }

    public void DeleteTrial(TrialRecord t)
    {
        trials.Remove(t);
        if (hovered == t) hovered = null;
    }

    // ---------- ghost outline ----------

    void UpdateGhost()
    {
        bool show = hovered != null && !hovered.shape.SameAs(rocket.shape);
        ghostTop.enabled = ghostBottom.enabled = show;
        if (!show) return;
        rocket.SampleOutline(hovered.shape, Samples, sTop, sBottom);
        SetLine(ghostTop, sTop);
        SetLine(ghostBottom, sBottom);
    }

    void SetLine(LineRenderer lr, Vector2[] pts)
    {
        float z = -(rocket.bodyRadius + 0.03f);      // in front of the model and the dots
        for (int i = 0; i < Samples; i++)
            linePoints[i] = rocket.transform.TransformPoint(new Vector3(pts[i].x, pts[i].y, z));
        lr.SetPositions(linePoints);
    }

    LineRenderer MakeLine(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = lineMaterial;
        lr.useWorldSpace = true;
        lr.positionCount = Samples;
        lr.widthMultiplier = 0.008f;
        lr.shadowCastingMode = ShadowCastingMode.Off;
        lr.enabled = false;
        mpb.SetColor("_BaseColor", ghostColor);
        lr.SetPropertyBlock(mpb);
        return lr;
    }

    // ---------- panel ----------

    void OnGUI()
    {
        if (rocket == null) return;
        EnsureStyles();
        Event e = Event.current;
        bool repaint = e.type == EventType.Repaint;
        if (repaint) hoverNext = null;

        Vector2 origin = new(12f, 12f);
        GUILayout.BeginArea(new Rect(origin.x, origin.y, panelWidth, Screen.height - 60f));
        GUILayout.BeginVertical(panel);

        TrialRecord toDelete = null;
        if (UIPanels.Header("trials", "TRIALS", head))
        {
            if (GUILayout.Button("Save & test in tunnel  [S]", button)) saveRequested = true;
            GUILayout.Space(4f);

            if (trials.Count == 0)
                GUILayout.Label("No trials yet. Shape the nose, then press S to save it and test it in the tunnel.", note);

            int shown = 0;
            for (int i = trials.Count - 1; i >= 0 && shown < maxShown; i--, shown++)   // newest first
            {
                var t = trials[i];
                Rect row = GUILayoutUtility.GetRect(panelWidth - 28f, 50f);
                bool over = row.Contains(e.mousePosition);
                bool current = t.shape.SameAs(rocket.shape);
                if (repaint && over) hoverNext = t;

                // 1. Draw (repaint only)
                if (repaint)
                {
                    GUI.color = new Color(1f, 1f, 1f, over ? 0.12f : 0.04f);
                    GUI.DrawTexture(row, white);
                    GUI.color = Color.white;

                    Rect thumb = new(row.x + 6f, row.y + 6f, 84f, row.height - 12f);
                    DrawThumb(t, thumb, current ? CurrentColor : ThumbColor);

                    float tx = thumb.xMax + 8f;
                    string label = (current ? "> " : "") + t.name + (t.hasResult ? "" : "  (untested)");
                    GUI.Label(new Rect(tx, row.y + 4f, row.xMax - tx - 24f, 20f), label, title);
                    GUI.Label(new Rect(tx, row.y + 25f, row.xMax - tx - 4f, 20f),
                              $"{t.noseAngle:0}° · blunt {t.bluntness:0.00}{ResultText(t)}", note);
                }

                // 2. Delete button (handled before the row click, so it wins)
                if (GUI.Button(new Rect(row.xMax - 22f, row.y + 4f, 18f, 18f), "×", small))
                    toDelete = t;
                // 3. Click the row to load it
                else if (e.type == EventType.MouseDown && e.button == 0 && over)
                {
                    LoadTrial(t);
                    e.Use();
                }
            }

            if (trials.Count > maxShown)
                GUILayout.Label($"+ {trials.Count - maxShown} older trials from this session", note);

            GUILayout.Space(4f);
            GUILayout.Label("Hover: compare  ·  Click: load (Z to undo)  ·  ×: delete", note);
        }

        GUILayout.EndVertical();
        if (repaint)
        {
            Rect r = GUILayoutUtility.GetLastRect();
            r.position += origin;
            UIPanels.Set(this, r);
            hovered = hoverNext;
        }
        GUILayout.EndArea();

        if (toDelete != null) DeleteTrial(toDelete);
        if (saveRequested) { saveRequested = false; SaveAndTest(); }
    }

    string ResultText(TrialRecord t)
    {
        return t.hasResult ? $" · smooth {t.smoothFlow * 100f:0}% @ M{t.mach:0.0}" : "";
    }

    // ---------- thumbnails ----------

    void DrawThumb(TrialRecord t, Rect r, Color color)
    {
        EnsureThumb(t);
        float cw = r.width / ThumbCols;
        float mid = r.center.y;
        float scale = r.height * 0.5f / ThumbYRange;

        GUI.color = color;
        for (int c = 0; c < ThumbCols; c++)
        {
            float top = t.thumbTop[c], bottom = t.thumbBottom[c];
            if (top - bottom <= 0.001f) continue;
            GUI.DrawTexture(new Rect(r.x + c * cw, mid - top * scale, cw + 0.5f, (top - bottom) * scale), white);
        }
        GUI.color = Color.white;
    }

    // Silhouette as column heights (in body radii): the nose from the saved outline, then a stub of body.
    void EnsureThumb(TrialRecord t)
    {
        if (t.thumbTop != null) return;
        rocket.SampleOutline(t.shape, Samples, sTop, sBottom);
        float R = rocket.bodyRadius;
        t.thumbTop = new float[ThumbCols];
        t.thumbBottom = new float[ThumbCols];

        for (int c = 0; c < ThumbCols; c++)
        {
            float x = Mathf.Lerp(ThumbXMin, ThumbXMax, (c + 0.5f) / ThumbCols) * R;
            if (x >= 0f) { t.thumbTop[c] = 1f; t.thumbBottom[c] = -1f; continue; }
            if (x < sTop[0].x) continue;                             // in front of the tip: empty

            int i = 0;
            while (i < Samples - 2 && sTop[i + 1].x < x) i++;
            float k = Mathf.InverseLerp(sTop[i].x, sTop[i + 1].x, x);
            t.thumbTop[c] = Mathf.Lerp(sTop[i].y, sTop[i + 1].y, k) / R;
            t.thumbBottom[c] = Mathf.Lerp(sBottom[i].y, sBottom[i + 1].y, k) / R;
        }
    }

    void EnsureStyles()
    {
        if (panel != null) return;
        white = Texture2D.whiteTexture;
        var bg = new Texture2D(1, 1);
        bg.SetPixel(0, 0, new Color(0.08f, 0.08f, 0.09f, 0.85f));
        bg.Apply();

        panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(14, 14, 12, 12) };
        panel.normal.background = bg;
        head = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
        head.normal.textColor = new Color(0.6f, 0.75f, 0.85f);
        title = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
        title.normal.textColor = Color.white;
        note = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
        note.normal.textColor = new Color(0.65f, 0.65f, 0.65f);
        button = new GUIStyle(GUI.skin.button) { fontSize = 12 };
        small = new GUIStyle(GUI.skin.button) { fontSize = 12, padding = new RectOffset(0, 0, 0, 2) };

        ShockwaveFonts.AsTitle(head);
        ShockwaveFonts.AsBody(title);
        ShockwaveFonts.AsBody(note);
        ShockwaveFonts.AsBody(button);
    }

    void OnDisable()
    {
        UIPanels.Clear(this);
        hovered = hoverNext = null;
        if (ghostTop) ghostTop.enabled = false;
        if (ghostBottom) ghostBottom.enabled = false;
    }
}