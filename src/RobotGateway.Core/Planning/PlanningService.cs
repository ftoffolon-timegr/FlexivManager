using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace RobotGateway.Core;

public sealed class PlanningOptions
{
    /// <summary>When true, edits from an unauthenticated user are refused (defense in depth behind the login).</summary>
    public bool RequireAuthenticatedUser { get; set; } = true;
}

public sealed record EditResult(bool Success, IReadOnlyList<string> Errors)
{
    public static EditResult Ok { get; } = new(true, []);
    public static EditResult Fail(params string[] errors) => new(false, errors);
}

/// <summary>
/// Remote configuration: jobs (code -> plan) and the work list of production orders.
/// Registered on the monitoring host only. It never talks to the robot: the robot plan list comes from
/// the snapshot published by the worker.
/// </summary>
public sealed class PlanningService(
    IPlanningStore store,
    SnapshotStore snapshots,
    IOptions<RobotWorkerOptions> robotOptions,
    IOptions<PlanningOptions> planningOptions,
    EventLog events,
    TimeProvider time)
{
    public int MaxVelocityScale => robotOptions.Value.MaxVelocityScale;

    /// <summary>Robot plan list as last read by the worker.</summary>
    public IReadOnlyList<string> RobotPlans => snapshots.Current.AvailablePlans;

    public IPlanningReader Reader => store;

    // ---- Jobs --------------------------------------------------------------------------------

    public Task<EditResult> CreateJobAsync(JobDraft draft, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "create job", async name =>
        {
            var d = PlanningValidator.Normalize(draft);
            var errors = PlanningValidator.Validate(d, MaxVelocityScale, RobotPlans.ToArray());
            if (errors.Count > 0) return new EditResult(false, errors);
            var job = await store.CreateJobAsync(d, name, ct);
            Log(EventLevel.Info, $"Job {job.Describe()} created by {name}.");
            return EditResult.Ok;
        });

    public Task<EditResult> UpdateJobAsync(int id, int expectedVersion, JobDraft draft, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "update job", async name =>
        {
            var d = PlanningValidator.Normalize(draft);
            var errors = PlanningValidator.Validate(d, MaxVelocityScale, RobotPlans.ToArray());
            if (errors.Count > 0) return new EditResult(false, errors);
            var job = await store.UpdateJobAsync(id, expectedVersion, d, name, ct);
            Log(EventLevel.Info, $"Job {job.Describe()} updated by {name}.");
            return EditResult.Ok;
        });

    public Task<EditResult> DeleteJobAsync(int id, int expectedVersion, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "delete job", async name =>
        {
            var job = await store.GetJobAsync(id, ct);
            await store.DeleteJobAsync(id, expectedVersion, name, ct);
            Log(EventLevel.Info, $"Job {job?.Describe() ?? id.ToString()} deleted by {name}.");
            return EditResult.Ok;
        });

    // ---- Orders ------------------------------------------------------------------------------

    public Task<EditResult> CreateOrderAsync(OrderDraft draft, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "create order", async name =>
        {
            var d = PlanningValidator.Normalize(draft);
            if (await ValidateOrder(d, ct) is { } invalid) return invalid;
            var order = await store.CreateOrderAsync(d, name, ct);
            Log(EventLevel.Info, $"Work list: {order.Describe()} added by {name}.");
            return EditResult.Ok;
        });

    public Task<EditResult> UpdateOrderAsync(int id, int expectedVersion, OrderDraft draft, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "update order", async name =>
        {
            var d = PlanningValidator.Normalize(draft);
            if (await ValidateOrder(d, ct) is { } invalid) return invalid;
            var order = await store.UpdateOrderAsync(id, expectedVersion, d, name, ct);
            Log(EventLevel.Info, $"Work list: {order.Describe()} updated by {name}.");
            return EditResult.Ok;
        });

    public Task<EditResult> MoveOrderAsync(int id, int direction, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "move order", async name =>
        {
            await store.MoveOrderAsync(id, Math.Sign(direction), name, ct);
            var order = await store.GetOrderAsync(id, ct);
            Log(EventLevel.Info, $"Work list: {order?.Describe() ?? id.ToString()} moved {(direction < 0 ? "up" : "down")} by {name}.");
            return EditResult.Ok;
        });

    public Task<EditResult> CancelOrderAsync(int id, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "cancel order", async name =>
        {
            var order = await store.CancelOrderAsync(id, name, ct);
            Log(EventLevel.Info, $"Work list: {order.Describe()} cancelled by {name}.");
            return EditResult.Ok;
        });

    private async Task<EditResult?> ValidateOrder(OrderDraft d, CancellationToken ct)
    {
        var errors = PlanningValidator.Validate(d).ToList();
        if (errors.Count == 0 && await store.FindJobByCodeAsync(d.ProductCode, ct) is null)
            errors.Add($"No job for product code '{d.ProductCode}': create the association first.");
        return errors.Count == 0 ? null : new EditResult(false, errors);
    }

    private async Task<EditResult> Edit(ClaimsPrincipal user, string action, Func<string, Task<EditResult>> edit)
    {
        if (planningOptions.Value.RequireAuthenticatedUser && user.Identity?.IsAuthenticated != true)
        {
            Log(EventLevel.Warning, $"Planning: {action} refused, user not authenticated.");
            return EditResult.Fail("Login required.");
        }
        try
        {
            return await edit(UserName(user));
        }
        catch (PlanningConflictException ex)
        {
            return EditResult.Fail(ex.Message);
        }
    }

    private static string UserName(ClaimsPrincipal user) =>
        user.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : "anonymous";

    private void Log(EventLevel level, string message) =>
        events.Add(new RobotEvent(time.GetUtcNow(), level, "Planning", message));
}
