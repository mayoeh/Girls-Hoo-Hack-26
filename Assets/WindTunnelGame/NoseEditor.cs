using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

// Drag the nose dots in the Game view. Mirroring and dot count are on the TOOLS panel (M and +/- also work).
// Dot index: 0 = tip, 1..n = shape points, last = shoulder (shown, not draggable).
public class NoseEditor : MonoBehaviour
{
    public RocketModel rocket;
    public Camera cam;
    public Material dotMaterial;

    [Header("Feel")]
    [Range(0f, 1f)] public float falloff = 0.25f;
    public bool keepTapered = true;
    public float pickRadiusPixels = 22f;
    public float dotSize = 0.035f;
    public float tipSnap = 0.05f;

    [Header("Keys")]
    public Key mirrorKey = Key.M;
    public Key addDotKey = Key.Equals;     // the +/= key
    public Key removeDotKey = Key.Minus;

    public event System.Action EditStarted, EditFinished;   // step 5 uses these for undo
    public bool IsDragging => dragIndex >= 0;

    static readonly int ColorId = Shader.PropertyToID("_BaseColor");
    static readonly Color TipColor = new(1f, 0.82f, 0.2f);
    static readonly Color PointColor = new(0.3f, 0.82f, 1f);
    static readonly Color BottomColor = new(1f, 0.45f, 0.8f);    // bottom dots while mirroring is off
    static readonly Color ShoulderColor = new(0.5f, 0.5f, 0.5f);

    Renderer[] top = new Renderer[0], bottom = new Renderer[0];
    MaterialPropertyBlock mpb;
    int hoverIndex = -1, dragIndex = -1;
    bool hoverBottom, dragBottom;
    NoseShape startShape;
    Vector2 startMouse;

    void Start()
    {
        if (cam == null) cam = Camera.main;
        mpb = new MaterialPropertyBlock();
        BuildDots();
    }

    void Update()
    {
        if (rocket == null || cam == null || Mouse.current == null) return;
        if (top.Length != rocket.ControlPointCount) BuildDots();

        var mouse = Mouse.current;
        Vector2 px = mouse.position.ReadValue();

        if (dragIndex < 0)
        {
            HandleKeys();
            hoverIndex = UIPanels.Contains(px) ? -1 : Pick(px, out hoverBottom);
            if (mouse.leftButton.wasPressedThisFrame && hoverIndex >= 0)
                BeginDrag(hoverIndex, hoverBottom, px);
        }
        else
        {
            float scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0f) falloff = Mathf.Clamp01(falloff + Mathf.Sign(scroll) * 0.05f);

            Drag(px);
            if (!mouse.leftButton.isPressed) EndDrag();
        }

        PlaceDots();
    }

    // Testing mode hides the dots and stops editing.
    public void SetEditing(bool on)
    {
        enabled = on;
        ShowDots(on);
        hoverIndex = -1;
    }

    // Shows or hides every dot this editor owns, including any left over from a rebuild.
    public void ShowDots(bool on)
    {
        foreach (var r in GetComponentsInChildren<Renderer>(true))
            if (r && r.enabled != on) r.enabled = on;
    }

    void OnDisable() => ShowDots(false);

    // ---------- keys ----------

    void HandleKeys()
    {
        var kb = Keyboard.current;
        if (kb == null || UIPanels.Typing) return;
        if (kb[mirrorKey].wasPressedThisFrame) ToggleMirror();
        if (kb[addDotKey].wasPressedThisFrame || kb[Key.NumpadPlus].wasPressedThisFrame) ChangePointCount(+1);
        if (kb[removeDotKey].wasPressedThisFrame || kb[Key.NumpadMinus].wasPressedThisFrame) ChangePointCount(-1);
    }

    public void ToggleMirror() => ApplyEdit(() => rocket.shape.mirrored = !rocket.shape.mirrored);

    public void ChangePointCount(int delta)
    {
        int n = rocket.shape.points.Count;
        ApplyEdit(() => rocket.SetPointCount(n + delta));
    }

    void ApplyEdit(System.Action edit)
    {
        EditStarted?.Invoke();
        edit();
        rocket.Rebuild();
        EditFinished?.Invoke();
        if (top.Length != rocket.ControlPointCount) BuildDots();
    }

    // ---------- dragging ----------

    void BeginDrag(int index, bool isBottom, Vector2 px)
    {
        dragIndex = index;
        dragBottom = isBottom;
        startShape = rocket.shape.Clone();
        startMouse = MouseLocal(px);
        EditStarted?.Invoke();
    }

    void Drag(Vector2 px)
    {
        Vector2 delta = MouseLocal(px) - startMouse;
        float R = rocket.bodyRadius;
        var shape = rocket.shape;

        if (dragIndex == 0)
        {
            // Tip: left/right stretches the nose, up/down bends it.
            shape.length = Mathf.Clamp(startShape.length - delta.x / R, NoseShape.MinLength, NoseShape.MaxLength);            float h = Mathf.Clamp(startShape.tipHeight + delta.y / R, -1f, 1f);
            shape.tipHeight = Mathf.Abs(h) < tipSnap ? 0f : h;
        }
        else
        {
            int k = dragIndex - 1;
            List<Vector2> p0 = startShape.Side(dragBottom);   // the side being edited, as it was when the drag began
            List<Vector2> p = shape.Side(dragBottom);
            float du = delta.x / (startShape.length * R);
            float dv = delta.y / R;

            for (int j = 0; j < p0.Count; j++)
            {
                float w = j == k ? 1f : Weight(Mathf.Abs(p0[j].x - p0[k].x));
                p[j] = new Vector2(p0[j].x, Mathf.Clamp01(p0[j].y + dv * w));
            }

            float lo = (k == 0 ? 0f : p0[k - 1].x) + NoseShape.Gap;
            float hi = (k == p0.Count - 1 ? 1f : p0[k + 1].x) - NoseShape.Gap;
            p[k] = new Vector2(Mathf.Clamp(p0[k].x + du, lo, hi), p[k].y);

            if (keepTapered) Taper(p, k);
        }

        rocket.Rebuild();
    }

    void EndDrag()
    {
        dragIndex = -1;
        EditFinished?.Invoke();
    }

    float Weight(float distance)
    {
        if (falloff < 0.01f) return 0f;
        float t = distance / falloff;
        return Mathf.Exp(-t * t);
    }

    static void Taper(List<Vector2> p, int k)
    {
        for (int j = k - 1; j >= 0; j--)
            p[j] = new Vector2(p[j].x, Mathf.Min(p[j].y, p[j + 1].y));
        for (int j = k + 1; j < p.Count; j++)
            p[j] = new Vector2(p[j].x, Mathf.Max(p[j].y, p[j - 1].y));
    }

    Vector2 MouseLocal(Vector2 px)
    {
        Ray ray = cam.ScreenPointToRay(px);
        var plane = new Plane(rocket.transform.forward, rocket.transform.position);
        plane.Raycast(ray, out float dist);
        Vector3 local = rocket.transform.InverseTransformPoint(ray.GetPoint(dist));
        return new Vector2(local.x, dragBottom ? -local.y : local.y);
    }

    // ---------- dots ----------

    int Pick(Vector2 px, out bool isBottom)
    {
        isBottom = false;
        int best = -1;
        float bestDist = pickRadiusPixels;
        for (int i = 0; i < rocket.ControlPointCount - 1; i++)
        {
            for (int side = 0; side < 2; side++)
            {
                if (i == 0 && side == 1) continue;
                Vector3 sp = cam.WorldToScreenPoint(DotWorld(i, side == 1));
                float d = Vector2.Distance(px, sp);
                if (d < bestDist) { bestDist = d; best = i; isBottom = side == 1; }
            }
        }
        return best;
    }

    Vector3 DotWorld(int i, bool isBottom)
    {
        Vector2 p = rocket.DotPosition(i, isBottom);
        float z = -(rocket.bodyRadius + 0.02f);
        return rocket.transform.TransformPoint(new Vector3(p.x, p.y, z));
    }

    void BuildDots()
    {
        foreach (var r in top) if (r) Destroy(r.gameObject);
        foreach (var r in bottom) if (r) Destroy(r.gameObject);

        int n = rocket.ControlPointCount;
        top = new Renderer[n];
        bottom = new Renderer[n];
        for (int i = 0; i < n; i++)
        {
            top[i] = MakeDot($"Dot {i} top");
            if (i > 0) bottom[i] = MakeDot($"Dot {i} bottom");
        }
    }

    Renderer MakeDot(string name)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(transform, false);
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = dotMaterial;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.enabled = enabled;   // never show dots while the editor is off (testing / final results)
        return r;
    }

    void PlaceDots()
    {
        int n = rocket.ControlPointCount;
        for (int i = 0; i < n; i++)
        {
            Place(top[i], i, false);
            if (bottom[i] != null) Place(bottom[i], i, true);
        }
    }

    void Place(Renderer r, int i, bool isBottom)
    {
        bool mirrored = rocket.shape.mirrored;
        bool sameSideAsDrag = mirrored || isBottom == dragBottom || i == 0;
        bool big = (i == hoverIndex && isBottom == hoverBottom && dragIndex < 0)
                || (i == dragIndex && sameSideAsDrag);

        r.transform.position = DotWorld(i, isBottom);
        r.transform.localScale = Vector3.one * dotSize * (big ? 1.4f : 1f);
        mpb.SetColor(ColorId, DotColor(i, isBottom, sameSideAsDrag));
        r.SetPropertyBlock(mpb);
    }

    Color DotColor(int i, bool isBottom, bool sameSideAsDrag)
    {
        int n = rocket.ControlPointCount;
        if (i == 0) return dragIndex == 0 ? Color.white : TipColor;
        if (i == n - 1) return ShoulderColor;

        Color c = isBottom && !rocket.shape.mirrored ? BottomColor : PointColor;
        if (dragIndex >= 1 && sameSideAsDrag)
        {
            var p0 = startShape.Side(dragBottom);
            float w = i == dragIndex ? 1f : Weight(Mathf.Abs(p0[i - 1].x - p0[dragIndex - 1].x));
            c = Color.Lerp(c, Color.white, w);
        }
        return c;
    }

}