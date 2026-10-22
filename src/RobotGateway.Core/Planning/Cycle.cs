namespace RobotGateway.Core;

public enum CycleOutcome
{
    Completed,
    Stopped,
    Failed,
}

/// <summary>What to run: the job version read at start, and the order it counts for (none for a one-off job).</summary>
public sealed record CycleRequest(Job Job, int? OrderId, string? OrderNumber);

/// <summary>The cycle currently executing, published in the snapshot.</summary>
public sealed record ActiveCycle(
    string ProductCode,
    string PlanName,
    int VelocityScale,
    int? OrderId,
    string? OrderNumber,
    string Source,
    DateTimeOffset StartedAt);

/// <summary>A finished cycle, published in the snapshot and stored in the cycle history.</summary>
public sealed record CycleRecord(
    string ProductCode,
    string PlanName,
    int VelocityScale,
    int? OrderId,
    string? OrderNumber,
    string Source,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    CycleOutcome Outcome,
    string? Message)
{
    public long Id { get; init; }
}
