using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RobotGateway.Core;

namespace RobotGateway.Data;

/// <summary>
/// SQLite persistence of jobs, orders and cycle history. Writes are serialized (single writer):
/// SQLite allows one writer anyway, and it keeps uniqueness and position checks race-free.
/// </summary>
public sealed class SqlitePlanningStore(IDbContextFactory<PlanningDbContext> factory, TimeProvider time) : IPlanningStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly SemaphoreSlim _write = new(1, 1);

    /// <summary>Creates the database file and schema if missing. Schema changes later need EF migrations.</summary>
    public void EnsureCreated()
    {
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    // ---- Reads -------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Job>> ListJobsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Jobs.AsNoTracking().OrderBy(j => j.ProductCode).ToListAsync(ct);
        return rows.Select(ToJob).ToList();
    }

    public async Task<Job?> GetJobAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        return row is null ? null : ToJob(row);
    }

    public async Task<Job?> FindJobByCodeAsync(string productCode, CancellationToken ct = default)
    {
        var code = ProductCode.Normalize(productCode);
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.ProductCode == code, ct);
        return row is null ? null : ToJob(row);
    }

    public async Task<IReadOnlyList<JobHistoryEntry>> GetJobHistoryAsync(int jobId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.JobHistory.AsNoTracking().Where(h => h.JobId == jobId).OrderByDescending(h => h.Id).ToListAsync(ct);
        return rows.Select(h => new JobHistoryEntry(h.JobId, h.Version, h.Action, h.ProductCode, h.User, h.At, h.Json)).ToList();
    }

    public async Task<IReadOnlyList<ProductionOrder>> ListOpenOrdersAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Active)
            .ToListAsync(ct);
        return rows
            .OrderBy(o => o.Status == OrderStatus.Active ? 0 : 1)
            .ThenBy(o => o.Position)
            .ThenBy(o => o.Id)
            .Select(ToOrder)
            .ToList();
    }

    public async Task<IReadOnlyList<ProductionOrder>> ListClosedOrdersAsync(int max, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Completed || o.Status == OrderStatus.Cancelled)
            .OrderByDescending(o => o.Id)
            .Take(max)
            .ToListAsync(ct);
        return rows.Select(ToOrder).ToList();
    }

    public async Task<ProductionOrder?> GetOrderAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        return row is null ? null : ToOrder(row);
    }

    public async Task<IReadOnlyList<CycleRecord>> ListCyclesAsync(int max, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Cycles.AsNoTracking().OrderByDescending(c => c.Id).Take(max).ToListAsync(ct);
        return rows.Select(c => new CycleRecord(c.ProductCode, c.PlanName, c.VelocityScale, c.OrderId, c.OrderNumber, c.Source,
            c.StartedAt, TimeSpan.FromSeconds(c.DurationSeconds), c.Outcome, c.Message) { Id = c.Id }).ToList();
    }

    // ---- Jobs --------------------------------------------------------------------------------

    public Task<Job> CreateJobAsync(JobDraft draft, string user, CancellationToken ct = default) => Write(async db =>
    {
        if (await db.Jobs.AnyAsync(j => j.ProductCode == draft.ProductCode, ct))
            throw new PlanningConflictException($"Product code '{draft.ProductCode}' already has a job.");
        var now = time.GetUtcNow();
        var row = new JobEntity { CreatedAt = now, Version = 1 };
        Apply(row, draft, user, now);
        db.Jobs.Add(row);
        await db.SaveChangesAsync(ct);
        var job = ToJob(row);
        await AddHistory(db, job, "Created", user, ct);
        return job;
    }, ct);

    public Task<Job> UpdateJobAsync(int id, int expectedVersion, JobDraft draft, string user, CancellationToken ct = default) => Write(async db =>
    {
        var row = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct)
            ?? throw new PlanningConflictException("The job was deleted in the meantime.");
        if (row.Version != expectedVersion)
            throw new PlanningConflictException($"The job was changed by {row.UpdatedBy} in the meantime: reload and retry.");
        if (await db.Jobs.AnyAsync(j => j.Id != id && j.ProductCode == draft.ProductCode, ct))
            throw new PlanningConflictException($"Product code '{draft.ProductCode}' already has a job.");
        if (row.ProductCode != draft.ProductCode && await HasOpenOrders(db, row.ProductCode, ct))
            throw new PlanningConflictException($"Open orders use code '{row.ProductCode}': it cannot be renamed.");

        Apply(row, draft, user, time.GetUtcNow());
        row.Version++;
        await SaveConcurrent(db, ct);
        var job = ToJob(row);
        await AddHistory(db, job, "Updated", user, ct);
        return job;
    }, ct);

    public Task DeleteJobAsync(int id, int expectedVersion, string user, CancellationToken ct = default) => Write(async db =>
    {
        var row = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct)
            ?? throw new PlanningConflictException("The job was already deleted.");
        if (row.Version != expectedVersion)
            throw new PlanningConflictException($"The job was changed by {row.UpdatedBy} in the meantime: reload and retry.");
        if (await HasOpenOrders(db, row.ProductCode, ct))
            throw new PlanningConflictException($"Open orders use code '{row.ProductCode}': cancel them first.");
        var job = ToJob(row);
        db.Jobs.Remove(row);
        await SaveConcurrent(db, ct);
        await AddHistory(db, job, "Deleted", user, ct);
        return 0;
    }, ct);

    // ---- Orders ------------------------------------------------------------------------------

    public Task<ProductionOrder> CreateOrderAsync(OrderDraft draft, string user, CancellationToken ct = default) => Write(async db =>
    {
        await EnsureOrderNumberFree(db, draft.OrderNumber, exceptId: null, ct);
        var now = time.GetUtcNow();
        var lastPosition = await db.Orders.MaxAsync(o => (int?)o.Position, ct) ?? 0;
        var row = new OrderEntity
        {
            OrderNumber = draft.OrderNumber,
            ProductCode = draft.ProductCode,
            Quantity = draft.Quantity,
            Notes = draft.Notes,
            Status = OrderStatus.Pending,
            Position = lastPosition + 1,
            Version = 1,
            CreatedAt = now,
            CreatedBy = user,
            UpdatedAt = now,
            UpdatedBy = user,
        };
        db.Orders.Add(row);
        await db.SaveChangesAsync(ct);
        return ToOrder(row);
    }, ct);

    public Task<ProductionOrder> UpdateOrderAsync(int id, int expectedVersion, OrderDraft draft, string user, CancellationToken ct = default) => Write(async db =>
    {
        var row = await LoadOrder(db, id, ct);
        if (row.Version != expectedVersion)
            throw new PlanningConflictException($"The order was changed by {row.UpdatedBy} in the meantime: reload and retry.");
        if (row.Status != OrderStatus.Pending)
            throw new PlanningConflictException($"Order {row.OrderNumber} is {row.Status}: only pending orders can be edited.");
        await EnsureOrderNumberFree(db, draft.OrderNumber, exceptId: id, ct);

        row.OrderNumber = draft.OrderNumber;
        row.ProductCode = draft.ProductCode;
        row.Quantity = draft.Quantity;
        row.Notes = draft.Notes;
        Touch(row, user);
        await SaveConcurrent(db, ct);
        return ToOrder(row);
    }, ct);

    public Task MoveOrderAsync(int id, int direction, string user, CancellationToken ct = default) => Write(async db =>
    {
        var row = await LoadOrder(db, id, ct);
        if (row.Status != OrderStatus.Pending)
            throw new PlanningConflictException($"Order {row.OrderNumber} is {row.Status}: only pending orders can be moved.");
        var pending = db.Orders.Where(o => o.Status == OrderStatus.Pending && o.Id != id);
        var neighbour = direction < 0
            ? await pending.Where(o => o.Position < row.Position).OrderByDescending(o => o.Position).FirstOrDefaultAsync(ct)
            : await pending.Where(o => o.Position > row.Position).OrderBy(o => o.Position).FirstOrDefaultAsync(ct);
        if (neighbour is null)
            return 0; // already first/last
        (row.Position, neighbour.Position) = (neighbour.Position, row.Position);
        Touch(row, user);
        Touch(neighbour, user);
        await SaveConcurrent(db, ct);
        return 0;
    }, ct);

    public Task<ProductionOrder> CancelOrderAsync(int id, string user, CancellationToken ct = default) => Write(async db =>
    {
        var row = await LoadOrder(db, id, ct);
        if (row.Status is not (OrderStatus.Pending or OrderStatus.Active))
            throw new PlanningConflictException($"Order {row.OrderNumber} is already {row.Status}.");
        row.Status = OrderStatus.Cancelled;
        Touch(row, user);
        await SaveConcurrent(db, ct);
        return ToOrder(row);
    }, ct);

    public Task MarkOrderActiveAsync(int id, string user, CancellationToken ct = default) => Write(async db =>
    {
        var row = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (row is { Status: OrderStatus.Pending })
        {
            row.Status = OrderStatus.Active;
            Touch(row, user);
            await SaveConcurrent(db, ct);
        }
        return 0;
    }, ct);

    public Task<ProductionOrder?> RecordCycleAsync(CycleRecord cycle, CancellationToken ct = default) => Write(async db =>
    {
        db.Cycles.Add(new CycleEntity
        {
            OrderId = cycle.OrderId,
            OrderNumber = cycle.OrderNumber,
            ProductCode = cycle.ProductCode,
            PlanName = cycle.PlanName,
            VelocityScale = cycle.VelocityScale,
            Source = cycle.Source,
            StartedAt = cycle.StartedAt,
            DurationSeconds = cycle.Duration.TotalSeconds,
            Outcome = cycle.Outcome,
            Message = cycle.Message,
        });

        OrderEntity? order = null;
        if (cycle.OrderId is { } orderId)
        {
            order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
            if (order is not null && cycle.Outcome == CycleOutcome.Completed)
            {
                // A cancelled order keeps its status; the cycle that was already running still counts as done.
                order.CompletedCount++;
                if (order.Status is OrderStatus.Pending or OrderStatus.Active)
                    order.Status = order.CompletedCount >= order.Quantity ? OrderStatus.Completed : OrderStatus.Active;
                Touch(order, cycle.Source);
            }
        }
        await SaveConcurrent(db, ct);
        return order is null ? null : ToOrder(order);
    }, ct);

    // ---- Helpers -----------------------------------------------------------------------------

    private async Task<T> Write<T>(Func<PlanningDbContext, Task<T>> action, CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await action(db);
        }
        finally
        {
            _write.Release();
        }
    }

    private static async Task SaveConcurrent(PlanningDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new PlanningConflictException("Changed in the meantime: reload and retry."); }
        catch (DbUpdateException ex) { throw new PlanningConflictException($"Save failed: {ex.InnerException?.Message ?? ex.Message}"); }
    }

    private static async Task<OrderEntity> LoadOrder(PlanningDbContext db, int id, CancellationToken ct) =>
        await db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct)
        ?? throw new PlanningConflictException("The order does not exist.");

    private static Task<bool> HasOpenOrders(PlanningDbContext db, string code, CancellationToken ct) =>
        db.Orders.AnyAsync(o => o.ProductCode == code && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Active), ct);

    private static async Task EnsureOrderNumberFree(PlanningDbContext db, string number, int? exceptId, CancellationToken ct)
    {
        var used = await db.Orders.AnyAsync(o => o.OrderNumber == number && o.Id != exceptId
            && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Active), ct);
        if (used)
            throw new PlanningConflictException($"Order number '{number}' is already used by an open order.");
    }

    private void Touch(OrderEntity row, string user)
    {
        row.UpdatedAt = time.GetUtcNow();
        row.UpdatedBy = user;
        row.Version++;
    }

    private static void Apply(JobEntity row, JobDraft draft, string user, DateTimeOffset now)
    {
        row.ProductCode = draft.ProductCode;
        row.PlanName = draft.PlanName;
        row.VelocityScale = draft.VelocityScale;
        row.Description = draft.Description;
        row.Enabled = draft.Enabled;
        row.UpdatedAt = now;
        row.UpdatedBy = user;
    }

    private async Task AddHistory(PlanningDbContext db, Job job, string action, string user, CancellationToken ct)
    {
        db.JobHistory.Add(new JobHistoryEntity
        {
            JobId = job.Id,
            Version = job.Version,
            Action = action,
            ProductCode = job.ProductCode,
            User = user,
            At = time.GetUtcNow(),
            Json = JsonSerializer.Serialize(job, Json),
        });
        await db.SaveChangesAsync(ct);
    }

    private static Job ToJob(JobEntity r) => new()
    {
        Id = r.Id,
        ProductCode = r.ProductCode,
        PlanName = r.PlanName,
        VelocityScale = r.VelocityScale,
        Description = r.Description,
        Enabled = r.Enabled,
        Version = r.Version,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        UpdatedBy = r.UpdatedBy,
    };

    private static ProductionOrder ToOrder(OrderEntity r) => new()
    {
        Id = r.Id,
        OrderNumber = r.OrderNumber,
        ProductCode = r.ProductCode,
        Quantity = r.Quantity,
        CompletedCount = r.CompletedCount,
        Status = r.Status,
        Notes = r.Notes,
        Position = r.Position,
        Version = r.Version,
        CreatedAt = r.CreatedAt,
        CreatedBy = r.CreatedBy,
        UpdatedAt = r.UpdatedAt,
        UpdatedBy = r.UpdatedBy,
    };
}
