using UnityEngine;

namespace E2E.Events
{
    public class E2ETarget : MonoBehaviour
    {
        public int hits;
        public E2EPayload last;

        public void Ping() => hits++;
    }
}
