namespace RobotGateway.Core;

public enum OrderStatus
{
    Pending,
    Active,
    Completed,
    Cancelled,

    /// <summary>Closed by "copy missing to another day": the remaining quantity now lives in a new row.</summary>
    CarriedOver,
}

/// <summary>
/// One row of the daily work plan: on <see cref="WorkDate"/>, run the job of <see cref="ProductCode"/>
/// <see cref="Quantity"/> times. The operator can run only open rows of today's plan, in any order.
/// </summary>
public sealed record ProductionOrder
{
    public int Id { get; init; }

    /// <summary>Day of the work plan this row belongs to (local date).</summary>
    public DateOnly WorkDate { get; init; }

    /// <summary>Optional reference (customer/production order); unique among open rows when given.</summary>
    public string OrderNumber { get; init; } = "";

    /// <summary>Normalized product code; resolved to its job at each cycle start.</summary>
    public required string ProductCode { get; init; }

    public int Quantity { get; init; }

    /// <summary>Pieces done: completed cycles of this row.</summary>
    public int CompletedCount { get; init; }

    public OrderStatus Status { get; init; }
    public string? Notes { get; init; }

    /// <summary>Sort key inside the day (ascending).</summary>
    public int Position { get; init; }

    public int Version { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedBy { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    public string UpdatedBy { get; init; } = "";

    public bool IsOpen => Status is OrderStatus.Pending or OrderStatus.Active;
    public int Remaining => Math.Max(0, Quantity - CompletedCount);

    /// <summary>Order number when given, otherwise the product code.</summary>
    public string Label => OrderNumber.Length > 0 ? OrderNumber : ProductCode;

    public string Describe() =>
        $"{WorkDate:dd/MM/yyyy} {(OrderNumber.Length > 0 ? $"ordine {OrderNumber} " : "")}'{ProductCode}' {CompletedCount}/{Quantity}";
}

/// <summary>Editable content of a plan row. A null <paramref name="WorkDate"/> means today.</summary>
public sealed record OrderDraft(string OrderNumber, string ProductCode, int Quantity, string? Notes, DateOnly? WorkDate = null);

/// <summary>Local calendar day used by the daily work plan.</summary>
public static class WorkDay
{
    public static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetLocalNow().DateTime);
}
