namespace RobotGateway.Core;

/// <summary>
/// Thread-safe, bounded in-memory log of gateway and robot events (newest last).
/// </summary>
public sealed class EventLog(int capacity = 500)
{
    private readonly Queue<RobotEvent> _events = new();
    private readonly Lock _sync = new();
    private long _version;

    /// <summary>Incremented on every append; lets readers skip work when nothing changed.</summary>
    public long Version => Interlocked.Read(ref _version);

    public void Add(RobotEvent e)
    {
        lock (_sync)
        {
            _events.Enqueue(e);
            while (_events.Count > capacity)
                _events.Dequeue();
            Interlocked.Increment(ref _version);
        }
    }

    public IReadOnlyList<RobotEvent> GetRecent(int max = 50)
    {
        lock (_sync)
            return _events.Skip(Math.Max(0, _events.Count - max)).ToArray();
    }
}
