using UnityEngine;

public class E2EShooter : MonoBehaviour
{
    public int power = 1;

    public E2EPayload Fire() => new E2EPayload { value = power };
}
