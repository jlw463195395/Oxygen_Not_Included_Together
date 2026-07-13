namespace Shared.Networking
{
    /// <summary>
    /// Evaluates snapshot freshness using only the receiver's monotonic clock.
    /// Sender wall-clock timestamps are suitable for ordering packets from the same
    /// sender, but not for measuring age across machines or process lifetimes.
    /// </summary>
    public static class SnapshotFreshness
    {
        public static bool IsStale(bool hasSnapshot, double receivedAt, double now, double staleAfter)
        {
            if (!hasSnapshot)
                return true;

            if (staleAfter < 0)
                throw new System.ArgumentOutOfRangeException(nameof(staleAfter));

            return now >= receivedAt && now - receivedAt > staleAfter;
        }
    }
}
