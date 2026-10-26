namespace RobotGateway.Flexiv;

/// <summary>
/// Passes on only robot log entries not seen before. Works whether event_log() returns the whole log or only
/// recent entries (to verify on the robot). Entries are identified by (timestamp, id).
/// </summary>
internal sealed class EventLogCursor
{
    private long _lastTimestamp = long.MinValue;
    private readonly HashSet<int> _idsAtLastTimestamp = [];

    public void Reset()
    {
        _lastTimestamp = long.MinValue;
        _idsAtLastTimestamp.Clear();
    }

    public List<T> TakeNew<T>(IEnumerable<T> entries, Func<T, long> timestamp, Func<T, int> id)
    {
        var fresh = new List<T>();
        foreach (var entry in entries.OrderBy(timestamp))
        {
            var ts = timestamp(entry);
            if (ts < _lastTimestamp) continue;
            if (ts > _lastTimestamp)
            {
                _lastTimestamp = ts;
                _idsAtLastTimestamp.Clear();
            }
            if (_idsAtLastTimestamp.Add(id(entry)))
                fresh.Add(entry);
        }
        return fresh;
    }
}
