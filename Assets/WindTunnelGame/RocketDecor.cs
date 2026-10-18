using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

// Dresses the wind tunnel model as a rocket: four swept tail fins in an X layout and a
// "NACA / MODEL n" label on the body (Mary Jackson worked for NACA, NASA's predecessor, at Langley).
// Purely visual: the flow model, the flow lines and the scores ignore it.
// The roll-pattern paint itself is in RocketModel (vertex colours), so it follows the mesh.
[RequireComponent(typeof(RocketModel))]
public class RocketDecor : MonoBehaviour
{
    [Header("Fins (sizes in body radii)")]
    public int finCount = 4;
    [Tooltip("Rotation of the first fin around the body. 45 = X layout, so no fin is edge-on to the camera.")]
    public float finRoll = 45f;
    public Color finColor = new(0.08f, 0.08f, 0.09f);
    public float rootChord = 1.5f, tipChord = 0.85f, span = 0.95f, sweep = 0.95f, thickness = 0.05f;

    [Header("Label")]
    [Tooltip("Astro Space SDF works well. Falls back to TextMesh Pro's default font if empty.")]
    public TMP_FontAsset labelFont;
    public Color labelColor = new(0.08f, 0.08f, 0.09f);
    [Tooltip("Where the label is centred along the body, in body radii behind the shoulder.")]
    public float labelAt = 2.45f;

    RocketModel rocket;
    Transform root;
    TextMeshPro title, subtitle;
    string label = "TRIAL RUN";

    void Start()
    {
        rocket = GetComponent<RocketModel>();
        Build();
    }

    // The smaller line under "NACA", e.g. "MODEL 3".
    public void SetLabel(string text)
    {
        if (text == label) return;
        label = text;
        if (subtitle != null) subtitle.text = text;
    }

    void Build()
    {
        if (root != null) Destroy(root.gameObject);
        root = new GameObject("Rocket Decor").transform;
        root.SetParent(transform, false);

        float R = rocket.bodyRadius, Lb = rocket.bodyLength;
        Material mat = GetComponent<Renderer>().sharedMaterial;   // same metal as the body (uses vertex colours)

        Mesh fin = FinMesh(R, Lb);
        for (int i = 0; i < finCount; i++)
        {
            var go = new GameObject($"Fin {i + 1}");
            go.transform.SetParent(root, false);
            go.transform.localRotation = Quaternion.AngleAxis(finRoll + 360f * i / finCount, Vector3.right);
            go.AddComponent<MeshFilter>().sharedMesh = fin;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
        }

        title = MakeText("NACA", labelAt * R, 0.2f * R, 2.6f * R, 0.46f * R);
        subtitle = MakeText(label, labelAt * R, -0.28f * R, 2.6f * R, 0.24f * R);
    }

    // One swept fin: x along the body, y outward from the axis, z = thickness.
    // Root on the body near the tail, tip swept back past the base like a classic rocket fin.
    Mesh FinMesh(float R, float Lb)
    {
        float y0 = 0.97f * R, y1 = R + span * R, t = 0.5f * thickness * R;
        float rootLE = Lb - rootChord * R, rootTE = Lb;
        float tipLE = rootLE + sweep * R, tipTE = tipLE + tipChord * R;
        Vector3 A(float z) => new(rootLE, y0, z);
        Vector3 B(float z) => new(rootTE, y0, z);
        Vector3 C(float z) => new(tipTE, y1, z);
        Vector3 D(float z) => new(tipLE, y1, z);

        var verts = new System.Collections.Generic.List<Vector3>();
        var tris = new System.Collections.Generic.List<int>();
        // Every face is added with both windings, so it shows from either side whatever the fin's rotation.
        void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
        {
            for (int side = 0; side < 2; side++)
            {
                int s = verts.Count;
                verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
                if (side == 0) { tris.Add(s); tris.Add(s + 1); tris.Add(s + 2); tris.Add(s); tris.Add(s + 2); tris.Add(s + 3); }
                else           { tris.Add(s); tris.Add(s + 2); tris.Add(s + 1); tris.Add(s); tris.Add(s + 3); tris.Add(s + 2); }
            }
        }
        Quad(A(-t), B(-t), C(-t), D(-t));   // front face
        Quad(A(t), B(t), C(t), D(t));       // back face
        Quad(B(-t), B(t), C(t), C(-t));     // trailing edge
        Quad(C(-t), C(t), D(t), D(-t));     // tip
        Quad(D(-t), D(t), A(t), A(-t));     // leading edge

        var colors = new Color[verts.Count];
        for (int i = 0; i < colors.Length; i++) colors[i] = finColor;
        var mesh = new Mesh { name = "Rocket Fin" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.colors = colors;
        mesh.uv = new Vector2[verts.Count];
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // Flat text on the camera side of the body, a hair in front of the surface. Auto-sized to its box.
    TextMeshPro MakeText(string text, float x, float y, float width, float height)
    {
        var go = new GameObject("Label " + text);
        go.transform.SetParent(root, false);
        go.transform.localPosition = new Vector3(x, y, -1.02f * rocket.bodyRadius);
        var tmp = go.AddComponent<TextMeshPro>();
        if (labelFont != null) tmp.font = labelFont;
        tmp.text = text;
        tmp.color = labelColor;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 0.05f;
        tmp.fontSizeMax = 100f;
        tmp.characterSpacing = 6f;
        tmp.rectTransform.sizeDelta = new Vector2(width, height);
        return tmp;
    }
}
