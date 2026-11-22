using UnityEditor;

[CustomEditor(typeof(E2EShooter))]
public class E2EShooterEditor : Editor
{
    public override void OnInspectorGUI() => DrawDefaultInspector();
}
