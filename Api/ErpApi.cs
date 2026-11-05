using FlexivManager.Hosting;
using RobotGateway.Core;

namespace FlexivManager.Api;

/// <summary>
/// Interface with the customer's ERP (see docs/INTEGRAZIONE-GESTIONALE.md).
/// <list type="bullet">
/// <item>GET /api/erp/production: what was produced today, per article code (both hosts, open like every GET).</item>
/// <item>GET /api/erp/articles: article codes received from the ERP and their association to a robot plan.</item>
/// <item>POST /api/erp/articles/import: reads the import folder now instead of waiting for the next scan
/// (monitoring host, API key).</item>
/// </list>
/// Nothing here can command the robot.
/// </summary>
public static class ErpApi
{
    public static IEndpointRouteBuilder MapErpReadApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/erp");

        // Without ?date=yyyy-MM-dd: today (local date of this PC).
        api.MapGet("/production", async (ProductionExportService export, DateOnly? date, CancellationToken ct) =>
            await export.BuildAsync(date, ct));

        api.MapGet("/articles", async (IErpStore erp, IPlanningReader planning, ArticleImporter importer, CancellationToken ct) =>
        {
            var jobs = (await planning.ListJobsAsync(ct)).ToDictionary(j => j.ProductCode, StringComparer.Ordinal);
            var articles = (await erp.ListArticlesAsync(ct))
                .Select(a => ArticleDto.From(a, jobs.GetValueOrDefault(a.Code)))
                .ToList();
            return new ArticlesDto(importer.Folder, importer.Options.Enabled, importer.LastScan, articles);
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapErpImportApi(this IEndpointRouteBuilder endpoints, ApiKeyOptions options)
    {
        var api = endpoints.MapGroup("/api/erp").RequireApiKey(options);

        api.MapPost("/articles/import", async (HttpContext http, ArticleImporter importer, CancellationToken ct) =>
        {
            var result = await importer.ScanAsync($"API {http.User.Identity?.Name}", manual: true, ct);
            return result.Error is null
                ? Results.Ok(result)
                : Results.Problem(result.Error, statusCode: StatusCodes.Status503ServiceUnavailable,
                    extensions: new Dictionary<string, object?> { ["scan"] = result });
        });

        return endpoints;
    }
}

/// <summary>An imported code; <see cref="PlanName"/> is null while no robot plan is associated.</summary>
public sealed record ArticleDto(
    string ArticleCode,
    string Code,
    DateTimeOffset FirstImportedAt,
    DateTimeOffset LastImportedAt,
    string SourceFile,
    string? PlanName,
    bool? Enabled)
{
    public static ArticleDto From(ArticleCode a, Job? job) =>
        new(a.RawCode, a.Code, a.FirstImportedAt, a.LastImportedAt, a.SourceFile, job?.PlanName, job?.Enabled);
}

public sealed record ArticlesDto(string ImportFolder, bool AutomaticScan, ArticleScanResult? LastScan, IReadOnlyList<ArticleDto> Articles);
