namespace RobotGateway.Core;

public enum OrderStatus
{
    Pending,
    Active,
    Completed,
    Cancelled,
}

/// <summary>
/// Production order in the work list: run the job of <see cref="ProductCode"/> <see cref="Quantity"/> times.
/// Open orders (Pending/Active) are worked in list order; Active orders stay on top.
/// </summary>
public sealed record ProductionOrder
{
    public int Id { get; init; }

    /// <summary>Unique among open orders.</summary>
    public required string OrderNumber { get; init; }

    /// <summary>Normalized product code; resolved to its job at each cycle start.</summary>
    public required string ProductCode { get; init; }

    public int Quantity { get; init; }
    public int CompletedCount { get; init; }
    public OrderStatus Status { get; init; }
    public string? Notes { get; init; }

    /// <summary>Sort key inside the work list (ascending).</summary>
    public int Position { get; init; }

    public int Version { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedBy { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    public string UpdatedBy { get; init; } = "";

    public bool IsOpen => Status is OrderStatus.Pending or OrderStatus.Active;
    public int Remaining => Math.Max(0, Quantity - CompletedCount);

    public string Describe() => $"order {OrderNumber} ('{ProductCode}', {CompletedCount}/{Quantity})";
}

/// <summary>Editable content of an order.</summary>
public sealed record OrderDraft(string OrderNumber, string ProductCode, int Quantity, string? Notes);
