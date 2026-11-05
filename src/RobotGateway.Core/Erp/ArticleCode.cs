namespace RobotGateway.Core;

/// <summary>
/// An article code received from the ERP. The planner associates it to a robot plan in the code editor
/// (<see cref="Job"/>, matched on <see cref="Code"/>).
/// </summary>
/// <param name="Code">Normalized code (<see cref="ProductCode.Normalize"/>): the key, same as <see cref="Job.ProductCode"/>.</param>
/// <param name="RawCode">The code exactly as the ERP wrote it (only blanks and control characters removed): used in the export.</param>
/// <param name="SourceFile">Name of the last file that contained the code.</param>
public sealed record ArticleCode(
    string Code,
    string RawCode,
    DateTimeOffset FirstImportedAt,
    DateTimeOffset LastImportedAt,
    string SourceFile);

/// <summary>Codes of one file once stored: <paramref name="Added"/> new, <paramref name="AlreadyKnown"/> already present.</summary>
public sealed record ArticleImportCounts(int Added, int AlreadyKnown);

/// <summary>Persistence used by the ERP interface (implemented by the SQLite planning store).</summary>
public interface IErpStore
{
    /// <summary>Every imported article code, by code.</summary>
    Task<IReadOnlyList<ArticleCode>> ListArticlesAsync(CancellationToken ct = default);

    /// <summary>
    /// Adds the new codes and refreshes the known ones (raw spelling, last import time, source file).
    /// Codes not in the list are kept: each file only adds codes.
    /// </summary>
    Task<ArticleImportCounts> ImportArticlesAsync(IReadOnlyList<ParsedArticle> codes, string sourceFile, CancellationToken ct = default);

    /// <summary>Cycles (any outcome) whose start is in [<paramref name="from"/>, <paramref name="to"/>), oldest first.</summary>
    Task<IReadOnlyList<CycleRecord>> ListCyclesStartedBetweenAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
