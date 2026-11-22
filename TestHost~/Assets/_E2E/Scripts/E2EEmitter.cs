using UnityEngine;
using UnityEngine.Events;

namespace E2E.Events
{
    public class E2EEmitter : MonoBehaviour
    {
        public UnityEvent onFire = new UnityEvent();
    }
}
