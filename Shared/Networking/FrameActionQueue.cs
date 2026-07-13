using System;
using System.Collections.Concurrent;

namespace Shared.Networking
{
    /// <summary>
    /// Thread-safe FIFO for work produced by transport threads and consumed in a
    /// bounded batch by Unity's main thread.
    /// </summary>
    public sealed class FrameActionQueue
    {
        private readonly ConcurrentQueue<Action> _actions = new ConcurrentQueue<Action>();

        public int Count => _actions.Count;

        public void Enqueue(Action action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            _actions.Enqueue(action);
        }

        public int Drain(int maxActions, Action<Exception>? onError = null)
        {
            if (maxActions <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxActions));

            int processed = 0;
            while (processed < maxActions && _actions.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    try
                    {
                        onError?.Invoke(ex);
                    }
                    catch
                    {
                        // The queue must keep draining even if diagnostics fail.
                    }
                }

                processed++;
            }

            return processed;
        }
    }
}
