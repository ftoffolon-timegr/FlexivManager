namespace RobotGateway.Core;

/// <summary>Read access to jobs, orders and cycle history (both hosts).</summary>
public interface IPlanningReader
{
    Task<IReadOnlyList<Job>> ListJobsAsync(CancellationToken ct = default);
    Task<Job?> GetJobAsync(int id, CancellationToken ct = default);

    /// <summary>The code is normalized first.</summary>
    Task<Job?> FindJobByCodeAsync(string productCode, CancellationToken ct = default);

    Task<IReadOnlyList<JobHistoryEntry>> GetJobHistoryAsync(int jobId, CancellationToken ct = default);

    /// <summary>Pending and Active orders in work-list order (Active first, then by position).</summary>
    Task<IReadOnlyList<ProductionOrder>> ListOpenOrdersAsync(CancellationToken ct = default);

    /// <summary>Completed and Cancelled orders, newest first.</summary>
    Task<IReadOnlyList<ProductionOrder>> ListClosedOrdersAsync(int max, CancellationToken ct = default);

    Task<ProductionOrder?> GetOrderAsync(int id, CancellationToken ct = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<CycleRecord>> ListCyclesAsync(int max, CancellationToken ct = default);
}

/// <summary>
/// Persistence. Job/order writes come from <see cref="PlanningService"/> (remote host, validated input);
/// order activation and cycle records come from <see cref="CycleRecorder"/> (operator host).
/// </summary>
public interface IPlanningStore : IPlanningReader
{
    Task<Job> CreateJobAsync(JobDraft draft, string user, CancellationToken ct = default);

    /// <exception cref="PlanningConflictException">Changed or deleted since <paramref name="expectedVersion"/>.</exception>
    Task<Job> UpdateJobAsync(int id, int expectedVersion, JobDraft draft, string user, CancellationToken ct = default);

    Task DeleteJobAsync(int id, int expectedVersion, string user, CancellationToken ct = default);

    Task<ProductionOrder> CreateOrderAsync(OrderDraft draft, string user, CancellationToken ct = default);

    /// <summary>Only Pending orders can be edited.</summary>
    Task<ProductionOrder> UpdateOrderAsync(int id, int expectedVersion, OrderDraft draft, string user, CancellationToken ct = default);

    /// <summary>Swaps a Pending order with the previous (-1) or next (+1) Pending order.</summary>
    Task MoveOrderAsync(int id, int direction, string user, CancellationToken ct = default);

    /// <summary>Pending or Active orders only. Does not affect a cycle already running.</summary>
    Task<ProductionOrder> CancelOrderAsync(int id, string user, CancellationToken ct = default);

    /// <summary>Pending -> Active when its first cycle starts. No-op for other statuses.</summary>
    Task MarkOrderActiveAsync(int id, string user, CancellationToken ct = default);

    /// <summary>
    /// Stores the cycle; a Completed cycle of an open order increments its count and completes the order
    /// when the quantity is reached. Returns the order after the update, if any.
    /// </summary>
    Task<ProductionOrder?> RecordCycleAsync(CycleRecord cycle, CancellationToken ct = default);
}

/// <summary>Invalid operation for the current data (conflict, duplicate, wrong status).</summary>
public sealed class PlanningConflictException(string message) : Exception(message);
