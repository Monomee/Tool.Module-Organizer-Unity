using UnityEngine;

public class FxA : MonoBehaviour
{
    public FxB b;
    public int Score() => FxOut.Bonus() + b.Value();
}
