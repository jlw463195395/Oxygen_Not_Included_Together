using ONI_Together.DebugTools;
using Shared.Networking;
using Shared.Profiling;
using System;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
    public class MainThreadExecutor : MonoBehaviour
    {
        private const int MaxActionsPerFrame = 128;

        public static MainThreadExecutor dispatcher;
        private readonly FrameActionQueue events = new FrameActionQueue();

        private void Awake()
        {
            using var _ = Profiler.Scope();

            if (dispatcher == null)
                dispatcher = this;
            else
                Destroy(this);
        }

        private void Update()
        {
            using var _ = Profiler.Scope();

            events.Drain(MaxActionsPerFrame, ex =>
                DebugConsole.LogError($"[Main/Thread] Queued action failed: {ex}"));
        }

        private void OnDestroy()
        {
            if (dispatcher == this)
                dispatcher = null;
        }

        public void QueueEvent(bool condition, Action action)
        {
            if (condition)
                events.Enqueue(action);
        }

        public void QueueEvent(Action action) => events.Enqueue(action);
    }
}
