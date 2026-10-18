using UnityEngine;
using UnityEngine.Rendering;

// A curved piece of a giant sphere: looks like the Earth's surface from orbit without needing a
// planet-sized model (which falls outside the camera's far clip and vanishes).
// The object's position is the TOP of the curve; the sphere's centre is `radius` units below it.
// Change the numbers in the Inspector and the mesh rebuilds instantly. Exports to glTF as a normal mesh.
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class EarthPatch : MonoBehaviour
{
    [Tooltip("Size of the imaginary planet. Bigger = flatter. Smaller = more curve.")]
    public float radius = 1500f;

    [Tooltip("How far the patch reaches from its centre, in degrees around the planet. Bigger = covers more.")]
    [Range(1f, 80f)] public float reach = 25f;

    [Tooltip("Grid detail. 128 is plenty.")]
    [Range(8, 256)] public int segments = 128;

    bool dirty = true;

    void OnEnable() { dirty = true; Build(); }
    void OnValidate() { dirty = true; }
    void Update() { if (dirty) Build(); }

    void Build()
    {
        dirty = false;
        int n = segments, row = n + 1;
        float t = Mathf.Tan(reach * Mathf.Deg2Rad);
        var verts = new Vector3[row * row];
        var norms = new Vector3[row * row];
        var uvs = new Vector2[row * row];

        for (int i = 0; i <= n; i++)
        for (int j = 0; j <= n; j++)
        {
            float u = Mathf.Lerp(-1f, 1f, i / (float)n);
            float v = Mathf.Lerp(-1f, 1f, j / (float)n);
            Vector3 dir = new Vector3(u * t, 1f, v * t).normalized;   // point on the sphere
            int k = i * row + j;
            verts[k] = dir * radius - Vector3.up * radius;           // top of the sphere sits at local 0,0,0
            norms[k] = dir;
            uvs[k] = new Vector2(u * 0.5f + 0.5f, v * 0.5f + 0.5f);
        }

        var tris = new int[n * n * 6];
        int q = 0;
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            int a = i * row + j, b = a + row, c = a + 1, d = b + 1;   // b = +u (x), c = +v (z)
            tris[q++] = a; tris[q++] = c; tris[q++] = b;
            tris[q++] = b; tris[q++] = c; tris[q++] = d;
        }

        var mf = GetComponent<MeshFilter>();
        var mesh = mf.sharedMesh;
        if (mesh == null || mesh.name != "EarthPatch") { mesh = new Mesh { name = "EarthPatch" }; mf.sharedMesh = mesh; }
        mesh.Clear();
        mesh.indexFormat = verts.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = verts; mesh.normals = norms; mesh.uv = uvs; mesh.triangles = tris;
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
    }
}
