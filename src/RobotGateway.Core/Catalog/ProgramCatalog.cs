using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RobotGateway.Core;

/// <summary>Read-only list of the work programs the operator is allowed to run.</summary>
public interface IProgramCatalog
{
    IReadOnlyList<WorkProgram> Programs { get; }

    /// <summary>Validation problems found while loading; invalid entries are excluded.</summary>
    IReadOnlyList<string> Errors { get; }

    WorkProgram? FindById(string id);

    WorkProgram? FindByPlanName(string planName);
}

/// <summary>
/// Validated, immutable program catalog. Loaded from a versioned JSON file
/// (<c>{ "schemaVersion": 1, "programs": [ ... ] }</c>); CRUD editing comes with the ProgramStore.
/// </summary>
public sealed partial class ProgramCatalog : IProgramCatalog
{
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ProgramCatalog(IEnumerable<WorkProgram> programs, IEnumerable<string>? loadErrors = null)
    {
        var errors = new List<string>(loadErrors ?? []);
        var valid = new List<WorkProgram>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var program in programs)
        {
            var problem = Validate(program);
            if (problem is null && !ids.Add(program.Id))
                problem = "duplicate id";
            if (problem is not null)
                errors.Add($"Program '{program.Id}': {problem}.");
            else
                valid.Add(program);
        }

        Programs = valid;
        Errors = errors;
    }

    public static ProgramCatalog Empty { get; } = new([]);

    public IReadOnlyList<WorkProgram> Programs { get; }
    public IReadOnlyList<string> Errors { get; }

    public WorkProgram? FindById(string id) =>
        Programs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public WorkProgram? FindByPlanName(string planName) =>
        Programs.FirstOrDefault(p => p.Kind == ProgramKind.ElementsPlan && p.PlanName == planName);

    /// <summary>Loads the catalog file. Never throws: problems end up in <see cref="Errors"/>.</summary>
    public static ProgramCatalog LoadJson(string path)
    {
        if (!File.Exists(path))
            return new ProgramCatalog([], [$"Catalog file not found: {path}"]);
        try
        {
            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize<CatalogFile>(stream, JsonOptions);
            if (file is null)
                return new ProgramCatalog([], [$"Catalog file is empty: {path}"]);
            if (file.SchemaVersion != SupportedSchemaVersion)
                return new ProgramCatalog([], [$"Unsupported catalog schemaVersion {file.SchemaVersion} (expected {SupportedSchemaVersion})."]);
            return new ProgramCatalog(file.Programs ?? []);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ProgramCatalog([], [$"Cannot read catalog {path}: {ex.Message}"]);
        }
    }

    private static string? Validate(WorkProgram p)
    {
        if (string.IsNullOrWhiteSpace(p.Id) || !IdPattern().IsMatch(p.Id)) return "id must match [A-Za-z0-9_-]{1,64}";
        if (string.IsNullOrWhiteSpace(p.Name)) return "name is required";
        if (p.Kind == ProgramKind.PrimitiveSequence) return "primitive sequences are not supported yet";
        if (string.IsNullOrWhiteSpace(p.PlanName)) return "planName is required for ElementsPlan";
        if (p.MaxVelocityScale is < CommandPolicy.MinVelocityScale or > CommandPolicy.MaxVelocityScale)
            return $"maxVelocityScale must be within {CommandPolicy.MinVelocityScale}..{CommandPolicy.MaxVelocityScale}";
        if (p.VelocityScale < CommandPolicy.MinVelocityScale || p.VelocityScale > p.MaxVelocityScale)
            return $"velocityScale must be within {CommandPolicy.MinVelocityScale}..maxVelocityScale";
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    private sealed record CatalogFile(int SchemaVersion, List<WorkProgram>? Programs);
}
