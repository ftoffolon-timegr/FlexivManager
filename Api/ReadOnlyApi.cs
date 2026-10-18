using RobotGateway.Core;

namespace FlexivManager.Api;

/// <summary>Read-only JSON API, mapped on both hosts. GET only: nothing here can command the robot.</summary>
public static class ReadOnlyApi
{
    /// <summary>A snapshot older than this means the worker loop is stuck.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(2);

    public static IEndpointRouteBuilder MapReadOnlyApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/status", (SnapshotStore store, TimeProvider time) =>
            StatusDto.From(store.Current, time.GetUtcNow()));

        api.MapGet("/telemetry", (SnapshotStore store) =>
            TelemetryDto.From(store.Current) is { } telemetry
                ? Results.Ok(telemetry)
                : Results.Problem("No telemetry: robot not connected.", statusCode: StatusCodes.Status503ServiceUnavailable));

        api.MapGet("/events", (EventLog events, int? max) =>
            events.GetRecent(Math.Clamp(max ?? 50, 1, 500)));

        api.MapGet("/programs", (IProgramCatalog catalog, SnapshotStore store) =>
        {
            var onRobot = store.Current.AvailablePlans;
            return catalog.Programs.Select(p => new ProgramDto(
                p.Id, p.Name, p.Description, p.PlanName, p.VelocityScale, p.MaxVelocityScale,
                p.PlanName is not null && onRobot.Contains(p.PlanName)));
        });

        return endpoints;
    }
}

public sealed record StatusDto(
    long Sequence,
    DateTimeOffset Timestamp,
    bool Stale,
    string Backend,
    RobotServiceState State,
    bool Connected,
    bool Operational,
    OperationalStatus OperationalStatus,
    bool Fault,
    bool EStopReleased,
    bool Busy,
    RobotMode Mode,
    string? CurrentPlan,
    string? PlanNode,
    DateTimeOffset? PlanStartedAt,
    long CompletedCycles,
    double? LastCycleSeconds,
    string? LastError,
    double LoopRateHz)
{
    public static StatusDto From(RobotSnapshot s, DateTimeOffset now) => new(
        s.Sequence,
        s.Timestamp,
        s.Sequence > 0 && now - s.Timestamp > ReadOnlyApi.StaleAfter,
        s.BackendName,
        s.State,
        s.Status.Connected,
        s.Status.Operational,
        s.Status.OperationalStatus,
        s.Status.Fault,
        s.Status.EStopReleased,
        s.Status.Busy,
        s.Status.Mode,
        s.CurrentPlan,
        string.IsNullOrEmpty(s.Plan?.NodeName) ? null : s.Plan.NodeName,
        s.CurrentPlanStartedAt,
        s.CompletedCycles,
        s.LastCycleTime?.TotalSeconds,
        s.LastError,
        Math.Round(s.LoopRateHz, 1));
}

/// <summary>Units as in <see cref="RobotStates"/>: rad, rad/s, Nm, degC, m + quaternion (w,x,y,z), N/Nm.</summary>
public sealed record TelemetryDto(
    long Sequence,
    DateTimeOffset Timestamp,
    double[] Q,
    double[] Dq,
    double[] Tau,
    double[] TauExt,
    double[] Temperature,
    double[] TcpPose,
    double[] ExtWrenchInTcp)
{
    public static TelemetryDto? From(RobotSnapshot s) => s.States is not { } st
        ? null
        : new(s.Sequence, s.Timestamp,
            [.. st.Q], [.. st.Dq], [.. st.Tau], [.. st.TauExt], [.. st.Temperature], [.. st.TcpPose], [.. st.ExtWrenchInTcp]);
}

public sealed record ProgramDto(
    string Id,
    string Name,
    string? Description,
    string? PlanName,
    int VelocityScale,
    int MaxVelocityScale,
    bool AvailableOnRobot);
