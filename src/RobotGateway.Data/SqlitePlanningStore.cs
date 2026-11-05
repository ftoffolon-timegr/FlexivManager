using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RobotGateway.Core;

namespace RobotGateway.Data;

/// <summary>
/// SQLite persistence of jobs, daily plan rows and cycle history. Writes are serialized (single writer):
/// SQLite allows one writer anyway, and it keeps uniqueness and position checks race-free.
/// </summary>
public sealed class SqlitePlanningStore(IDbContextFactory<PlanningDbContext> factory, TimeProvider time) : IPlanningStore, IErpStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly SemaphoreSlim _write = new(1, 1);

    /// <summary>Creates the database file and schema if missing. Schema changes later need EF migrations.</summary>
    public void EnsureCreated()
    {
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        DropObsoleteColumns(db);
        AddDailyPlanColumns(db);
        AddArticlesTable(db);
    }

    /// <summary>
    /// ERP article codes (2026-11-05). EnsureCreated does not touch an existing database, so the table is added
    /// here; same layout EF would create for <see cref="ArticleEntity"/>.
    /// </summary>
    private static void AddArticlesTable(PlanningDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "Articles" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Articles" PRIMARY KEY AUTOINCREMENT,
                "Code" TEXT NOT NULL,
                "RawCode" TEXT NOT NULL,
                "FirstImportedAt" TEXT NOT NULL,
                "LastImportedAt" TEXT NOT NULL,
                "SourceFile" TEXT NOT NULL
            )
            """);
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Articles_Code\" ON \"Articles\" (\"Code\")");
    }

    /// <summary>
    /// Databases created before the daily work plan (2026-10-30) lack these columns. Existing rows are put
    /// in the plan of the day they were created.
    /// </summary>
    private static void AddDailyPlanColumns(PlanningDbContext db)
    {
        AddColumn(db, "Jobs", "IsService", "INTEGER NOT NULL DEFAULT 0");
        if (AddColumn(db, "Orders", "WorkDate", "TEXT NOT NULL DEFAULT '0001-01-01'"))
            db.Database.ExecuteSqlRaw("UPDATE \"Orders\" SET \"WorkDate\" = substr(\"CreatedAt\", 1, 10)");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_Orders_WorkDate_Position\" ON \"Orders\" (\"WorkDate\", \"Position\")");
    }

    /// <returns>True when the column was missing and has been added.</returns>
    private static bool AddColumn(PlanningDbContext db, string table, string column, string definition)
    {
        if (HasColumn(db, table, column))
            return false;
        db.Database.ExecuteSqlRaw("ALTER TABLE \"" + table + "\" ADD COLUMN \"" + column + "\" " + definition);
        return true;
    }

    /// <summary>
    /// Columns of older databases that the model no longer has (SQLite supports DROP COLUMN since 3.35):
    /// VelocityScale (removed 2026-10-30, NOT NULL: every insert would fail) and the quality check of the
    /// pieces (OK / KO / Rework, removed 2026-11-03).
    /// </summary>
    private static void DropObsoleteColumns(PlanningDbContext db)
    {
        DropColumn(db, "Jobs", "VelocityScale");
        DropColumn(db, "Cycles", "VelocityScale");
        foreach (var column in new[] { "OkCount", "KoCount", "ReworkCount" })
            DropColumn(db, "Orders", column);
        foreach (var column in new[] { "Quality", "QualityNote", "QualityAt", "QualityBy" })
            DropColumn(db, "Cycles", column);
    }

    private static void DropColumn(PlanningDbContext db, string table, string column)
    {
        if (HasColumn(db, table, column))
            db.Database.ExecuteSqlRaw("ALTER TABLE \"" + table + "\" DROP COLUMN \"" + column + "\"");
    }

    private static bool HasColumn(PlanningDbContext db, string table, string column) =>
        db.Database.SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info({table}) WHERE name = {column}")
            .AsEnumerable().Single() > 0;

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
            .OrderBy(o => o.WorkDate)
            .ThenBy(o => o.Position)
            .ThenBy(o => o.Id)
            .Select(ToOrder)
            .ToList();
    }

    public async Task<IReadOnlyList<ProductionOrder>> ListPlanAsync(DateOnly date, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Orders.AsNoTracking().Where(o => o.WorkDate == date).ToListAsync(ct);
        return rows.OrderBy(o => o.Position).ThenBy(o => o.Id).Select(ToOrder).ToList();
    }

    public async Task<IReadOnlyList<ProductionOrder>> ListClosedOrdersAsync(int max, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Completed || o.Status == OrderStatus.Cancelled || o.Status == OrderStatus.CarriedOver)
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
        return rows.Select(ToCycle).ToList();
    }

    public async Task<CycleRecord?> GetCycleAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Cycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return row is null ? null : ToCycle(row);
    }

    // ---- Jobs --------------------------------------------------------------------------------

    public Task<Job> CreateJobAsync(JobDraft draft, string user, CancellationToken ct = default) => Write(async db =>
    {
        if (await db.Jobs.AnyAsync(j => j.ProductCode == draft.ProductCode, ct))
            throw new PlanningConflictException($"Il codice '{draft.ProductCode}' esiste già.");
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
            ?? throw new PlanningConflictException("Il codice è stato eliminato nel frattempo.");
        if (row.Version != expectedVersion)
            throw new PlanningConflictException($"Il codice è stato modificato da {row.UpdatedBy} nel frattempo: ricarica e riprova.");
        if (await db.Jobs.AnyAsync(j => j.Id != id && j.ProductCode == draft.ProductCode, ct))
            throw new PlanningConflictException($"Il codice '{draft.ProductCode}' esiste già.");
        if (row.ProductCode != draft.ProductCode && await HasOpenOrders(db, row.ProductCode, ct))
            throw new PlanningConflictException($"Ci sono ordini aperti con il codice '{row.ProductCode}': non si può rinominare.");

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
            ?? throw new PlanningConflictException("Il codice era già stato eliminato.");
        if (row.Version != expectedVersion)
            throw new PlanningConflictException($"Il codice è stato modificato da {row.UpdatedBy} nel frattempo: ricarica e riprova.");
        if (await HasOpenOrders(db, row.ProductCode, ct))
            throw new PlanningConflictException($"Ci sono ordini aperti con il codice '{row.ProductCode}': annullali prima.");
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
        var date = draft.WorkDate ?? WorkDay.Today(time);
        var row = new OrderEntity
        {
            WorkDate = date,
            OrderNumber = draft.OrderNumber,
            ProductCode = draft.ProductCode,
            Quantity = draft.Quantity,
            Notes = draft.Notes,
            Status = OrderStatus.Pending,
            Position = await NextPosition(db, date, ct),
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
            throw new PlanningConflictException($"L'ordine è stato modificato da {row.UpdatedBy} nel frattempo: ricarica e riprova.");
        if (row.Status != OrderStatus.Pending)
            throw new PlanningConflictException($"L'ordine {Label(row)} è {Labels.Of(row.Status)}: si possono modificare solo gli ordini da iniziare.");
        await EnsureOrderNumberFree(db, draft.OrderNumber, exceptId: id, ct);

        var date = draft.WorkDate ?? row.WorkDate;
        if (date != row.WorkDate)
        {
            row.Position = await NextPosition(db, date, ct);
            row.WorkDate = date;
        }
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
            throw new PlanningConflictException($"L'ordine {Label(row)} è {Labels.Of(row.Status)}: si possono spostare solo gli ordini da iniziare.");
        var pending = db.Orders.Where(o => o.WorkDate == row.WorkDate && o.Status == OrderStatus.Pending && o.Id != id);
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
            throw new PlanningConflictException($"L'ordine {Label(row)} è già {Labels.Of(row.Status)}.");
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
                // A closed row keeps its status; the cycle that was already running still counts as done.
                order.CompletedCount++;
                UpdateStatus(order);
                Touch(order, cycle.Source);
            }
        }
        await SaveConcurrent(db, ct);
        return order is null ? null : ToOrder(order);
    }, ct);

    public Task<IReadOnlyList<ProductionOrder>> CopyMissingAsync(DateOnly from, DateOnly to, string user, CancellationToken ct = default) => Write(async db =>
    {
        var open = await db.Orders
            .Where(o => o.WorkDate == from && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Active))
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        var position = await NextPosition(db, to, ct);
        var created = new List<OrderEntity>();
        foreach (var source in open.Where(o => o.CompletedCount < o.Quantity).OrderBy(o => o.Position).ThenBy(o => o.Id))
        {
            created.Add(new OrderEntity
            {
                WorkDate = to,
                OrderNumber = source.OrderNumber,
                ProductCode = source.ProductCode,
                Quantity = source.Quantity - source.CompletedCount,
                Notes = source.Notes,
                Status = OrderStatus.Pending,
                Position = position++,
                Version = 1,
                CreatedAt = now,
                CreatedBy = user,
                UpdatedAt = now,
                UpdatedBy = user,
            });
            source.Status = OrderStatus.CarriedOver;
            Touch(source, user);
        }
        db.Orders.AddRange(created);
        await SaveConcurrent(db, ct);
        return (IReadOnlyList<ProductionOrder>)created.Select(ToOrder).ToList();
    }, ct);

    // ---- Helpers -----------------------------------------------------------------------------

    // ---- ERP ---------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ArticleCode>> ListArticlesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Articles.AsNoTracking().OrderBy(a => a.Code).ToListAsync(ct);
        return rows.Select(a => new ArticleCode(a.Code, a.RawCode, a.FirstImportedAt, a.LastImportedAt, a.SourceFile)).ToList();
    }

    public Task<ArticleImportCounts> ImportArticlesAsync(IReadOnlyList<ParsedArticle> codes, string sourceFile, CancellationToken ct = default) => Write(async db =>
    {
        var now = time.GetUtcNow();
        var wanted = codes.Select(c => c.Code).ToList();
        var known = await db.Articles.Where(a => wanted.Contains(a.Code)).ToDictionaryAsync(a => a.Code, StringComparer.Ordinal, ct);
        var added = 0;
        foreach (var code in codes)
        {
            if (known.TryGetValue(code.Code, out var row))
            {
                row.RawCode = code.RawCode;
                row.LastImportedAt = now;
                row.SourceFile = sourceFile;
            }
            else
            {
                db.Articles.Add(new ArticleEntity
                {
                    Code = code.Code, RawCode = code.RawCode, FirstImportedAt = now, LastImportedAt = now, SourceFile = sourceFile,
                });
                added++;
            }
        }
        await db.SaveChangesAsync(ct);
        return new ArticleImportCounts(added, codes.Count - added);
    }, ct);

    public async Task<IReadOnlyList<CycleRecord>> ListCyclesStartedBetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        // SQLite cannot compare DateTimeOffset values server side: walk the history newest first (ids grow with
        // time) and stop well before the window, allowing for cycles stored out of start order.
        var stopBefore = from - TimeSpan.FromDays(1);
        await using var db = await factory.CreateDbContextAsync(ct);
        var result = new List<CycleRecord>();
        await foreach (var c in db.Cycles.AsNoTracking().OrderByDescending(c => c.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (c.StartedAt < stopBefore) break;
            if (c.StartedAt >= from && c.StartedAt < to) result.Add(ToCycle(c));
        }
        result.Reverse();
        return result.OrderBy(c => c.StartedAt).ToList();
    }

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
        catch (DbUpdateConcurrencyException) { throw new PlanningConflictException("Modificato nel frattempo: ricarica e riprova."); }
        catch (DbUpdateException ex) { throw new PlanningConflictException($"Salvataggio fallito: {ex.InnerException?.Message ?? ex.Message}"); }
    }

    private static async Task<OrderEntity> LoadOrder(PlanningDbContext db, int id, CancellationToken ct) =>
        await db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct)
        ?? throw new PlanningConflictException("L'ordine di produzione non esiste.");

    private static async Task<int> NextPosition(PlanningDbContext db, DateOnly date, CancellationToken ct) =>
        (await db.Orders.Where(o => o.WorkDate == date).MaxAsync(o => (int?)o.Position, ct) ?? 0) + 1;

    private static string Label(OrderEntity row) => row.OrderNumber.Length > 0 ? row.OrderNumber : row.ProductCode;

    /// <summary>Open/Completed follow the done count; closed rows keep their status.</summary>
    private static void UpdateStatus(OrderEntity order)
    {
        if (order.Status is not (OrderStatus.Pending or OrderStatus.Active or OrderStatus.Completed))
            return;
        order.Status = order.CompletedCount >= order.Quantity ? OrderStatus.Completed
            : order.CompletedCount > 0 || order.Status != OrderStatus.Pending ? OrderStatus.Active
            : OrderStatus.Pending;
    }

    private static Task<bool> HasOpenOrders(PlanningDbContext db, string code, CancellationToken ct) =>
        db.Orders.AnyAsync(o => o.ProductCode == code && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Active), ct);

    private static async Task EnsureOrderNumberFree(PlanningDbContext db, string number, int? exceptId, CancellationToken ct)
    {
        if (number.Length == 0)
            return; // the order number is optional
        var used = await db.Orders.AnyAsync(o => o.OrderNumber == number && o.Id != exceptId
            && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Active), ct);
        if (used)
            throw new PlanningConflictException($"Il numero ordine '{number}' è già usato da un ordine aperto.");
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
        row.Description = draft.Description;
        row.Enabled = draft.Enabled;
        row.IsService = draft.IsService;
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
        Description = r.Description,
        Enabled = r.Enabled,
        IsService = r.IsService,
        Version = r.Version,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        UpdatedBy = r.UpdatedBy,
    };

    private static ProductionOrder ToOrder(OrderEntity r) => new()
    {
        Id = r.Id,
        WorkDate = r.WorkDate,
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

    private static CycleRecord ToCycle(CycleEntity c) =>
        new(c.ProductCode, c.PlanName, c.OrderId, c.OrderNumber, c.Source,
            c.StartedAt, TimeSpan.FromSeconds(c.DurationSeconds), c.Outcome, c.Message)
        {
            Id = c.Id,
        };
}
