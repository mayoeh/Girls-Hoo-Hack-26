using UnityEditor;
using UnityEngine;

// Ctrl+Alt+G : drop a CamGuide_NN where the Scene view camera is, facing where it looks.
// Ctrl+Alt+U : move the selected guide to the current Scene view camera (re-aim it).
// Ctrl+Alt+V : look through the selected guide (jump the Scene view to it).
public static class CamGuideTool
{
    const string GroupName = "CamGuides";

    [MenuItem("Tools/Cam Guides/Add Guide Here %&g")]
    static void AddHere()
    {
        var view = SceneView.lastActiveSceneView;
        if (view == null) { Debug.LogWarning("Click into the Scene view first."); return; }

        var group = GameObject.Find(GroupName);
        if (group == null)
        {
            group = new GameObject(GroupName);
            var root = GameObject.Find("SiteMap_Root");
            if (root != null) group.transform.SetParent(root.transform, false);
            Undo.RegisterCreatedObjectUndo(group, "Create CamGuides");
        }

        var go = new GameObject($"CamGuide_{group.transform.childCount + 1:00}");
        Undo.RegisterCreatedObjectUndo(go, "Add Cam Guide");
        go.transform.SetParent(group.transform, false);
        go.transform.SetPositionAndRotation(view.camera.transform.position, view.camera.transform.rotation);
        go.AddComponent<CamGuide>();
        Selection.activeGameObject = null;   // stay in the Scene view so you can keep flying
        Debug.Log($"Added {go.name}");
    }

    [MenuItem("Tools/Cam Guides/Move Selected Guide Here %&u")]
    static void MoveSelected()
    {
        var view = SceneView.lastActiveSceneView;
        var t = Selection.activeTransform;
        if (view == null || t == null) return;
        Undo.RecordObject(t, "Move Cam Guide");
        t.SetPositionAndRotation(view.camera.transform.position, view.camera.transform.rotation);
    }

    [MenuItem("Tools/Cam Guides/View Through Selected Guide %&v")]
    static void ViewThrough()
    {
        var view = SceneView.lastActiveSceneView;
        var t = Selection.activeTransform;
        if (view == null || t == null) return;
        view.AlignViewToObject(t);
    }

    [MenuItem("Tools/Cam Guides/Renumber Guides")]
    static void Renumber()
    {
        var group = GameObject.Find(GroupName);
        if (group == null) return;
        for (int i = 0; i < group.transform.childCount; i++)
        {
            var c = group.transform.GetChild(i).gameObject;
            Undo.RecordObject(c, "Renumber Cam Guides");
            c.name = $"CamGuide_{i + 1:00}";
        }
    }
    [MenuItem("Tools/Cam Guides/Add Earth Below Selected Guide")]
    static void AddEarth()
    {
        var guide = Selection.activeTransform;
        if (guide == null) { Debug.LogWarning("Select a CamGuide first (e.g. CamGuide_09)."); return; }

        var go = new GameObject("Earth", typeof(MeshFilter), typeof(MeshRenderer));
        Undo.RegisterCreatedObjectUndo(go, "Add Earth");
        var env = GameObject.Find("Env") ?? GameObject.Find("SiteMap_Root");
        if (env != null) go.transform.SetParent(env.transform, true);

        // Put the top of the curve 30 units under the guide, so the horizon curves away in its view.
        go.transform.position = guide.position - Vector3.up * 30f;
        go.transform.rotation = Quaternion.Euler(0f, guide.eulerAngles.y, 0f);
        go.AddComponent<EarthPatch>();

        const string matPath = "Assets/SiteMap/Earth_Mat.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            mat = new Material(shader);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.1f);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.1f);
            AssetDatabase.CreateAsset(mat, matPath);
        }
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        Selection.activeGameObject = go;
        Debug.Log("Added Earth under " + guide.name + ". Drag your texture onto Earth_Mat's Base Map.");
    }
    // Select exactly two stars, then run this: makes a thin cylinder stretched between them.
    [MenuItem("Tools/Cam Guides/Connect Two Selected Stars")]
    static void ConnectStars()
    {
        var sel = Selection.transforms;
        if (sel.Length != 2) { Debug.LogWarning("Select exactly two stars (Ctrl+click)."); return; }
        Vector3 a = sel[0].position, b = sel[1].position;

        var parent = sel[0].parent;
        int n = 1;
        while (parent != null && parent.Find($"ConLine_{n}") != null) n++;

        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Undo.RegisterCreatedObjectUndo(go, "Connect Stars");
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.name = $"ConLine_{n}";
        if (parent != null) go.transform.SetParent(parent, true);
        go.transform.position = (a + b) * 0.5f;
        go.transform.rotation = Quaternion.FromToRotation(Vector3.up, (b - a).normalized);
        float thick = 0.04f;
        var ps = go.transform.parent != null ? go.transform.parent.lossyScale : Vector3.one;
        go.transform.localScale = new Vector3(thick / ps.x, Vector3.Distance(a, b) * 0.5f / ps.y, thick / ps.z);   // Unity cylinders are 2 units tall

        var r = go.GetComponent<MeshRenderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        var mat = AssetDatabase.LoadAssetAtPath<Material>($"Assets/SiteMap/Mat_ConLine_{n}.mat");
        if (mat != null) r.sharedMaterial = mat;
        Selection.activeGameObject = go;
    }
}
