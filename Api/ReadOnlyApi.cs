using RobotGateway.Core;

namespace FlexivManager.Api;

/// <summary>
/// Read-only JSON API, mapped on both hosts and open (no login, no key). GET only: nothing here can command
/// the robot or edit data. Planning writes are in <see cref="PlanningApi"/> (monitoring host, API key).
/// </summary>
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
                : Results.Problem("Nessuna telemetria: robot non connesso.", statusCode: StatusCodes.Status503ServiceUnavailable));

        api.MapGet("/events", (EventLog events, int? max) =>
            events.GetRecent(Math.Clamp(max ?? 50, 1, 500)));

        api.MapGet("/plans", (SnapshotStore store) => store.Current.AvailablePlans);

        api.MapGet("/jobs", async (IPlanningReader planning, SnapshotStore store, CancellationToken ct) =>
        {
            var onRobot = store.Current.AvailablePlans;
            return (await planning.ListJobsAsync(ct)).Select(j => JobDto.From(j, onRobot));
        });

        api.MapGet("/jobs/{id:int}", async (IPlanningReader planning, SnapshotStore store, int id, CancellationToken ct) =>
            await planning.GetJobAsync(id, ct) is { } job
                ? Results.Ok(JobDto.From(job, store.Current.AvailablePlans))
                : Results.NotFound());

        // Daily work plan and its report. Without ?date=yyyy-MM-dd: today (local date of this PC).
        api.MapGet("/plan", async (IPlanningReader planning, TimeProvider time, DateOnly? date, CancellationToken ct) =>
            await PlanDto.LoadAsync(planning, date ?? WorkDay.Today(time), ct));

        api.MapGet("/plan/rows/{id:int}", async (IPlanningReader planning, int id, CancellationToken ct) =>
            await planning.GetOrderAsync(id, ct) is { } row ? Results.Ok(row) : Results.NotFound());

        api.MapGet("/report", async (IPlanningReader planning, TimeProvider time, DateOnly? date, CancellationToken ct) =>
        {
            var day = date ?? WorkDay.Today(time);
            return DailyReport.Build(day, await planning.ListPlanAsync(day, ct));
        });

        api.MapGet("/orders", async (IPlanningReader planning, CancellationToken ct) => new
        {
            Open = await planning.ListOpenOrdersAsync(ct),
            Closed = await planning.ListClosedOrdersAsync(50, ct),
        });

        api.MapGet("/cycles", async (IPlanningReader planning, int? max, CancellationToken ct) =>
            await planning.ListCyclesAsync(Math.Clamp(max ?? 50, 1, 500), ct));

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
    ActiveCycle? ActiveCycle,
    string? PlanNode,
    CycleRecord? LastCycle,
    long CompletedCycles,
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
        s.ActiveCycle,
        string.IsNullOrEmpty(s.Plan?.NodeName) ? null : s.Plan.NodeName,
        s.LastCycle,
        s.CompletedCycles,
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

public sealed record JobDto(
    int Id,
    string ProductCode,
    string PlanName,
    string? Description,
    bool Enabled,
    bool IsService,
    bool PlanOnRobot,
    int Version,
    DateTimeOffset UpdatedAt,
    string UpdatedBy)
{
    public static JobDto From(Job j, IReadOnlyList<string> robotPlans) => new(
        j.Id, j.ProductCode, j.PlanName, j.Description, j.Enabled, j.IsService,
        robotPlans.Contains(j.PlanName), j.Version, j.UpdatedAt, j.UpdatedBy);
}

/// <summary>Every row of the work plan of one day, by position.</summary>
public sealed record PlanDto(DateOnly Date, IReadOnlyList<ProductionOrder> Rows)
{
    public static async Task<PlanDto> LoadAsync(IPlanningReader planning, DateOnly date, CancellationToken ct) =>
        new(date, await planning.ListPlanAsync(date, ct));
}
