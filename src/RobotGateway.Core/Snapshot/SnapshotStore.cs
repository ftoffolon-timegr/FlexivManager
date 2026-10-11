namespace RobotGateway.Core;

/// <summary>
/// Holds the latest <see cref="RobotSnapshot"/>. Single writer (the worker), many lock-free readers.
/// </summary>
public sealed class SnapshotStore
{
    private RobotSnapshot _current = RobotSnapshot.Initial;

    public RobotSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Raised on the worker thread after each publish. Handlers must be fast and non-blocking
    /// (e.g. just signal or marshal); exceptions are swallowed so they cannot stall the worker.
    /// </summary>
    public event Action<RobotSnapshot>? SnapshotUpdated;

    public void Publish(RobotSnapshot snapshot)
    {
        Interlocked.Exchange(ref _current, snapshot);

        var handlers = SnapshotUpdated;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<RobotSnapshot>>())
        {
            try { handler(snapshot); }
            catch { /* a faulty subscriber must never stop the robot loop */ }
        }
    }
}
