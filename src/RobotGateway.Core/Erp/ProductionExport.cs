namespace RobotGateway.Core;

public enum ArticleProgress
{
    /// <summary>Being machined: a cycle is running, or one of today's orders of the code is started and not finished.</summary>
    InProgress,

    /// <summary>Nothing in progress for the code: start, end, duration and quantity are final (until it is restarted).</summary>
    Completed,
}

/// <summary>
/// One article code worked on the day. While <see cref="Status"/> is InProgress only <see cref="StartedAt"/> is
/// given (the other values are null). Times are local time with offset.
/// </summary>
/// <param name="ArticleCode">The code as the ERP sent it (as typed in the code editor when it never came from the ERP).</param>
/// <param name="StartedAt">Start of the first cycle of the day.</param>
/// <param name="EndedAt">End of the last cycle of the day.</param>
/// <param name="DurationSeconds">Sum of the durations of the completed cycles (robot machining time), whole seconds.</param>
/// <param name="Quantity">Pieces produced: completed cycles.</param>
/// <param name="RobotProgram">Robot plan of the last cycle (JSON "robotProgram").</param>
public sealed record ArticleProduction(
    string ArticleCode,
    ArticleProgress Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int? DurationSeconds,
    int? Quantity,
    string RobotProgram);

/// <summary>Body of GET /api/erp/production.</summary>
public sealed record ProductionExport(DateOnly Date, DateTimeOffset GeneratedAt, IReadOnlyList<ArticleProduction> Articles)
{
    /// <summary>
    /// Builds the export of <paramref name="date"/> from its cycles. Service codes (e.g. HOME) are left out.
    /// Articles are listed by first start.
    /// </summary>
    /// <param name="cycles">Cycles started on <paramref name="date"/> (any outcome).</param>
    /// <param name="plan">Orders of <paramref name="date"/>.</param>
    /// <param name="running">The cycle running now, if any.</param>
    public static ProductionExport Build(
        DateOnly date,
        DateTimeOffset now,
        TimeZoneInfo zone,
        IEnumerable<CycleRecord> cycles,
        IEnumerable<ProductionOrder> plan,
        ActiveCycle? running,
        IEnumerable<Job> jobs,
        IEnumerable<ArticleCode> articles)
    {
        var serviceCodes = jobs.Where(j => j.IsService).Select(j => j.ProductCode).ToHashSet(StringComparer.Ordinal);
        var rawCodes = articles.ToDictionary(a => a.Code, a => a.RawCode, StringComparer.Ordinal);
        var openStarted = plan
            .Where(o => o.Status == OrderStatus.Active && o.Remaining > 0)
            .Select(o => o.ProductCode)
            .ToHashSet(StringComparer.Ordinal);
        var runningToday = running is not null && LocalDate(running.StartedAt, zone) == date ? running : null;

        DateTimeOffset Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone);

        var byCode = cycles
            .Where(c => !serviceCodes.Contains(c.ProductCode))
            .GroupBy(c => c.ProductCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.StartedAt).ToList(), StringComparer.Ordinal);
        if (runningToday is not null && !serviceCodes.Contains(runningToday.ProductCode))
            byCode.TryAdd(runningToday.ProductCode, []);

        var result = new List<ArticleProduction>();
        foreach (var (code, list) in byCode)
        {
            var isRunning = runningToday?.ProductCode == code;
            var started = list.Count > 0 ? list[0].StartedAt : runningToday!.StartedAt;
            if (isRunning && runningToday!.StartedAt < started)
                started = runningToday.StartedAt;
            var article = rawCodes.GetValueOrDefault(code, code);
            var planName = isRunning ? runningToday!.PlanName : list[^1].PlanName;

            if (isRunning || openStarted.Contains(code))
            {
                result.Add(new ArticleProduction(article, ArticleProgress.InProgress, Local(started), null, null, null, planName));
                continue;
            }
            var completed = list.Where(c => c.Outcome == CycleOutcome.Completed).ToList();
            var ended = list.Max(c => c.StartedAt + c.Duration);
            result.Add(new ArticleProduction(
                article,
                ArticleProgress.Completed,
                Local(started),
                Local(ended),
                (int)Math.Round(completed.Sum(c => c.Duration.TotalSeconds), MidpointRounding.AwayFromZero),
                completed.Count,
                planName));
        }
        return new ProductionExport(date, Local(now), result.OrderBy(a => a.StartedAt).ThenBy(a => a.ArticleCode, StringComparer.Ordinal).ToList());
    }

    public static DateOnly LocalDate(DateTimeOffset t, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, zone).DateTime);

    /// <summary>[start, end) of the local day <paramref name="date"/>, as instants.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) DayBounds(DateOnly date, TimeZoneInfo zone)
    {
        DateTimeOffset At(DateOnly d)
        {
            var local = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }
        return (At(date), At(date.AddDays(1)));
    }
}

/// <summary>Reads what the export needs and builds it (both hosts, read-only).</summary>
public sealed class ProductionExportService(IPlanningReader planning, IErpStore erp, SnapshotStore snapshots, TimeProvider time)
{
    public async Task<ProductionExport> BuildAsync(DateOnly? date = null, CancellationToken ct = default)
    {
        var zone = time.LocalTimeZone;
        var day = date ?? WorkDay.Today(time);
        var (from, to) = ProductionExport.DayBounds(day, zone);
        var cycles = await erp.ListCyclesStartedBetweenAsync(from, to, ct);
        var plan = await planning.ListPlanAsync(day, ct);
        var jobs = await planning.ListJobsAsync(ct);
        var articles = await erp.ListArticlesAsync(ct);
        return ProductionExport.Build(day, time.GetUtcNow(), zone, cycles, plan, snapshots.Current.ActiveCycle, jobs, articles);
    }
}
