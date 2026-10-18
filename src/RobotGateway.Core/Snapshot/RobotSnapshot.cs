using System.Collections.Immutable;

namespace RobotGateway.Core;

/// <summary>
/// Immutable view of the robot published by the worker each cycle. Readers never touch the backend.
/// </summary>
public sealed record RobotSnapshot
{
    public long Sequence { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string BackendName { get; init; } = "";
    public RobotServiceState State { get; init; } = RobotServiceState.Disconnected;
    public BackendStatus Status { get; init; }
    public RobotStates? States { get; init; }
    public PlanInfo? Plan { get; init; }
    public string? CurrentPlan { get; init; }
    public DateTimeOffset? CurrentPlanStartedAt { get; init; }
    public ImmutableArray<string> AvailablePlans { get; init; } = [];
    public long CompletedCycles { get; init; }
    public TimeSpan? LastCycleTime { get; init; }
    public string? LastError { get; init; }
    public double LoopRateHz { get; init; }

    public static RobotSnapshot Initial { get; } = new();
}
