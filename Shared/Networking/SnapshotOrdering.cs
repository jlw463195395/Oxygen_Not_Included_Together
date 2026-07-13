namespace Shared.Networking
{
    /// <summary>
    /// Enforces strictly increasing snapshot order. Equal timestamps are duplicates
    /// and must not refresh receiver-local freshness.
    /// </summary>
    public static class SnapshotOrdering
    {
        public static bool IsStrictlyNewer(long currentTimestamp, long incomingTimestamp)
        {
            return incomingTimestamp > currentTimestamp;
        }
    }
}
