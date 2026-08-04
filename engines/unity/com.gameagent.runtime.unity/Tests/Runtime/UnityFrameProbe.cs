using UnityEngine;

namespace GameAgent.Unity.Tests
{
    public sealed class UnityFrameProbe : MonoBehaviour
    {
        public bool WasUpdated { get; private set; }

        private void Update()
        {
            WasUpdated = true;
        }
    }
}
