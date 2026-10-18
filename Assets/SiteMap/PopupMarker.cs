using UnityEngine;

// Marker for a website popup card. Draws a gold diamond and its name in the Scene view.
// Editor-only visuals; the glTF export ignores this script (only the empty object's name + position matter).
public class PopupMarker : MonoBehaviour
{
    void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.85f, 0.4f, 0.95f);
        float s = 6f;
#if UNITY_EDITOR
        var view = UnityEditor.SceneView.currentDrawingSceneView;
        if (view != null) s = UnityEditor.HandleUtility.GetHandleSize(transform.position) * 0.25f;
#endif
        Vector3 p = transform.position;
        Gizmos.DrawLine(p + Vector3.up * s, p + Vector3.right * s);
        Gizmos.DrawLine(p + Vector3.right * s, p + Vector3.down * s);
        Gizmos.DrawLine(p + Vector3.down * s, p + Vector3.left * s);
        Gizmos.DrawLine(p + Vector3.left * s, p + Vector3.up * s);
        Gizmos.DrawLine(p + Vector3.forward * s, p + Vector3.back * s);
#if UNITY_EDITOR
        UnityEditor.Handles.color = Gizmos.color;
        UnityEditor.Handles.Label(p + Vector3.up * s * 1.4f, name);
#endif
    }
}
