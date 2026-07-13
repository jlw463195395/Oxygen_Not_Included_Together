using Shared.Networking;

var tests = new (string Name, Action Run)[]
{
    ("missing snapshot is stale", () => AssertTrue(SnapshotFreshness.IsStale(false, 0, 10, 2))),
    ("recent local receipt is fresh", () => AssertFalse(SnapshotFreshness.IsStale(true, 8.5, 10, 2))),
    ("receipt at threshold remains fresh", () => AssertFalse(SnapshotFreshness.IsStale(true, 8, 10, 2))),
    ("receipt older than threshold is stale", () => AssertTrue(SnapshotFreshness.IsStale(true, 7.999, 10, 2))),
    ("local clock rollback does not mark stale", () => AssertFalse(SnapshotFreshness.IsStale(true, 11, 10, 2))),
    ("older snapshot is rejected", () => AssertFalse(SnapshotOrdering.IsStrictlyNewer(100, 99))),
    ("duplicate snapshot is rejected", () => AssertFalse(SnapshotOrdering.IsStrictlyNewer(100, 100))),
    ("newer snapshot is accepted", () => AssertTrue(SnapshotOrdering.IsStrictlyNewer(100, 101))),
    ("frame queue preserves FIFO order", FrameQueuePreservesOrder),
    ("frame queue enforces per-drain budget", FrameQueueEnforcesBudget),
    ("frame queue continues after action failure", FrameQueueContinuesAfterFailure),
    ("frame queue survives error-handler failure", FrameQueueSurvivesErrorHandlerFailure),
    ("frame queue accepts concurrent producers", FrameQueueAcceptsConcurrentProducers),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT {tests.Length - failures} passed, {failures} failed");
return failures == 0 ? 0 : 1;

static void FrameQueuePreservesOrder()
{
    var queue = new FrameActionQueue();
    var observed = new List<int>();
    queue.Enqueue(() => observed.Add(1));
    queue.Enqueue(() => observed.Add(2));
    queue.Enqueue(() => observed.Add(3));

    AssertEqual(3, queue.Drain(10));
    AssertSequence(new[] { 1, 2, 3 }, observed);
    AssertEqual(0, queue.Count);
}

static void FrameQueueEnforcesBudget()
{
    var queue = new FrameActionQueue();
    var executed = 0;
    for (var i = 0; i < 5; i++)
        queue.Enqueue(() => executed++);

    AssertEqual(2, queue.Drain(2));
    AssertEqual(2, executed);
    AssertEqual(3, queue.Count);
    AssertEqual(3, queue.Drain(10));
    AssertEqual(5, executed);
}

static void FrameQueueContinuesAfterFailure()
{
    var queue = new FrameActionQueue();
    var executed = false;
    Exception? observed = null;
    queue.Enqueue(() => throw new InvalidOperationException("boom"));
    queue.Enqueue(() => executed = true);

    AssertEqual(2, queue.Drain(10, ex => observed = ex));
    AssertTrue(executed);
    AssertTrue(observed is InvalidOperationException);
}

static void FrameQueueSurvivesErrorHandlerFailure()
{
    var queue = new FrameActionQueue();
    var executed = false;
    queue.Enqueue(() => throw new InvalidOperationException("action failed"));
    queue.Enqueue(() => executed = true);

    AssertEqual(2, queue.Drain(10, _ => throw new InvalidOperationException("logger failed")));
    AssertTrue(executed);
}

static void FrameQueueAcceptsConcurrentProducers()
{
    var queue = new FrameActionQueue();
    var executed = 0;
    Parallel.For(0, 1000, _ => queue.Enqueue(() => Interlocked.Increment(ref executed)));

    AssertEqual(1000, queue.Count);
    AssertEqual(1000, queue.Drain(1000));
    AssertEqual(1000, executed);
}

static void AssertEqual<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected {expected}, got {actual}");
}

static void AssertSequence(IReadOnlyList<int> expected, IReadOnlyList<int> actual)
{
    if (expected.Count != actual.Count)
        throw new InvalidOperationException($"expected {expected.Count} items, got {actual.Count}");

    for (var i = 0; i < expected.Count; i++)
        AssertEqual(expected[i], actual[i]);
}

static void AssertTrue(bool value)
{
    if (!value)
        throw new InvalidOperationException("expected true");
}

static void AssertFalse(bool value)
{
    if (value)
        throw new InvalidOperationException("expected false");
}
