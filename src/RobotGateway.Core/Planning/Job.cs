namespace RobotGateway.Core;

/// <summary>
/// Association product/job code -> one robot plan (stored in Flexiv Elements) with its velocity.
/// Several codes may point to the same plan. Immutable: a running cycle keeps the version it started with.
/// </summary>
public sealed record Job
{
    public int Id { get; init; }

    /// <summary>Normalized product code (see <see cref="ProductCode.Normalize"/>), unique.</summary>
    public required string ProductCode { get; init; }

    public required string PlanName { get; init; }
    public int VelocityScale { get; init; } = 100;
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>Incremented on every save; optimistic concurrency and audit.</summary>
    public int Version { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string UpdatedBy { get; init; } = "";

    public string Describe() => $"'{ProductCode}' -> {PlanName} @ {VelocityScale} % (v{Version})";
}

/// <summary>Editable content of a job, as submitted by the editor.</summary>
public sealed record JobDraft(string ProductCode, string PlanName, int VelocityScale, string? Description, bool Enabled);

/// <summary>Audit trail: one entry per create/update/delete, with the saved content as JSON.</summary>
public sealed record JobHistoryEntry(int JobId, int Version, string Action, string ProductCode, string User, DateTimeOffset At, string Json);

/// <summary>Product code rules shared by the editor and the barcode scanner.</summary>
public static class ProductCode
{
    public const int MaxLength = 64;

    /// <summary>
    /// Removes control characters (CR/LF/TAB/GS sent by keyboard-wedge scanners), trims and upper-cases.
    /// Codes are therefore case-insensitive.
    /// </summary>
    public static string Normalize(string? raw) =>
        raw is null ? "" : new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim().ToUpperInvariant();

    /// <returns>Null when the normalized code is valid, otherwise the reason.</returns>
    public static string? Validate(string normalized)
    {
        if (normalized.Length == 0) return "Product code is required.";
        if (normalized.Length > MaxLength) return $"Product code is longer than {MaxLength} characters.";
        if (normalized.Any(c => c is < ' ' or > '~')) return "Product code may contain printable ASCII characters only.";
        return null;
    }
}
