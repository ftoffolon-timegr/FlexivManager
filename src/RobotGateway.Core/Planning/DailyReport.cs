namespace RobotGateway.Core;

/// <summary>Progress of one plan row as shown in the daily report.</summary>
public enum RowProgress
{
    /// <summary>Nothing done yet (Pending).</summary>
    Missing,

    /// <summary>Started, pieces still to do (Active).</summary>
    InProgress,

    /// <summary>Quantity reached (Completed).</summary>
    Done,

    /// <summary>Cancelled by the planner: what was not done is no longer expected.</summary>
    Cancelled,

    /// <summary>Closed by "copy missing": the remaining pieces were moved to another day.</summary>
    CarriedOver,
}

public sealed record DailyReportRow(ProductionOrder Row, RowProgress Progress);

/// <summary>Piece counts of a day (or of one row).</summary>
/// <param name="Planned">Quantities of the rows that were not cancelled.</param>
/// <param name="Missing">Pieces still to do on rows that were not cancelled (including those carried over).</param>
public sealed record DailyReportTotals(
    int Rows,
    int RowsDone,
    int RowsInProgress,
    int RowsMissing,
    int Planned,
    int Done,
    int Missing);

/// <summary>
/// Progress of the work plan of one day: rows classified as done / in progress / missing, with piece counts.
/// Computed from the plan rows only, so the web page and the REST API always agree.
/// </summary>
public sealed record DailyReport(DateOnly Date, IReadOnlyList<DailyReportRow> Rows, DailyReportTotals Totals)
{
    /// <param name="rows">Every row of the plan of <paramref name="date"/>, by position.</param>
    public static DailyReport Build(DateOnly date, IEnumerable<ProductionOrder> rows)
    {
        var list = rows.Select(r => new DailyReportRow(r, ProgressOf(r))).ToList();
        var counted = list.Where(r => r.Progress != RowProgress.Cancelled).Select(r => r.Row).ToList();
        var totals = new DailyReportTotals(
            Rows: list.Count,
            RowsDone: list.Count(r => r.Progress == RowProgress.Done),
            RowsInProgress: list.Count(r => r.Progress == RowProgress.InProgress),
            RowsMissing: list.Count(r => r.Progress is RowProgress.Missing or RowProgress.CarriedOver),
            Planned: counted.Sum(r => r.Quantity),
            Done: list.Sum(r => r.Row.CompletedCount),
            Missing: counted.Sum(r => r.Remaining));
        return new DailyReport(date, list, totals);
    }

    public static RowProgress ProgressOf(ProductionOrder row) => row.Status switch
    {
        OrderStatus.Completed => RowProgress.Done,
        OrderStatus.Cancelled => RowProgress.Cancelled,
        OrderStatus.CarriedOver => RowProgress.CarriedOver,
        _ when row.CompletedCount > 0 || row.Status == OrderStatus.Active => RowProgress.InProgress,
        _ => RowProgress.Missing,
    };
}
