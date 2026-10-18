using UnityEngine;

// Marker for a camera position on the website flythrough. Draws a little camera shape and a line
// to the next guide in the Scene view. Editor-only visuals; the glTF export ignores this script.
public class CamGuide : MonoBehaviour
{
    void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.9f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawFrustum(Vector3.zero, 50f, 1.2f, 0.05f, 16f / 9f);
        Gizmos.matrix = Matrix4x4.identity;

        var parent = transform.parent;
        if (parent == null) return;
        int i = transform.GetSiblingIndex();
        if (i + 1 < parent.childCount)
        {
            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.5f);
            Gizmos.DrawLine(transform.position, parent.GetChild(i + 1).position);
        }
    }
}
