namespace RobotGateway.Core;

public enum EventLevel
{
    Info,
    Warning,
    Error,
}

public sealed record RobotEvent(DateTimeOffset Timestamp, EventLevel Level, string Source, string Message);
