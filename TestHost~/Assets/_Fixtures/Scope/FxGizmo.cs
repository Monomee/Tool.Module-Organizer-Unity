using UnityEngine;

public class FxGizmo : MonoBehaviour
{
    void OnDrawGizmos()
    {
#if UNITY_EDITOR
        UnityEditor.Handles.Label(transform.position, "gizmo");
#endif
    }
}
