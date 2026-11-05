using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FlexivManager.Hosting;
using RobotGateway.Core;

namespace FlexivManager.Api;

/// <summary>
/// REST write API for planning (jobs, daily work plan), mapped on the monitoring host only and protected by the
/// API key in the <c>X-Api-Key</c> header. Every call goes through <see cref="PlanningService"/>, exactly like
/// the web pages: same validation, same event log. Reading is in <see cref="ReadOnlyApi"/> and stays open.
/// </summary>
public static class PlanningApi
{
    /// <summary>Authentication type of the principal built from a valid API key.</summary>
    public const string AuthenticationType = "ApiKey";

    public static IEndpointRouteBuilder MapPlanningApi(this IEndpointRouteBuilder endpoints, ApiKeyOptions options)
    {
        var api = endpoints.MapGroup("/api").RequireApiKey(options);

        // ---- Jobs (code -> plan) ---------------------------------------------------------------

        api.MapPost("/jobs", async (HttpContext http, PlanningService planning, JobRequest request, CancellationToken ct) =>
        {
            var result = await planning.CreateJobAsync(request.ToDraft(), http.User, ct);
            if (!result.Success) return Failed(result);
            var job = await planning.Reader.GetJobAsync(result.Id!.Value, ct);
            return Results.Created($"/api/jobs/{result.Id}", job is null ? null : JobDto.From(job, planning.RobotPlans));
        });

        api.MapPut("/jobs/{id:int}", async (HttpContext http, PlanningService planning, int id, JobRequest request, CancellationToken ct) =>
        {
            if (request.Version is not { } version) return VersionRequired();
            var result = await planning.UpdateJobAsync(id, version, request.ToDraft(), http.User, ct);
            if (!result.Success) return Failed(result);
            var job = await planning.Reader.GetJobAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(JobDto.From(job, planning.RobotPlans));
        });

        api.MapDelete("/jobs/{id:int}", async (HttpContext http, PlanningService planning, int id, int version, CancellationToken ct) =>
        {
            var result = await planning.DeleteJobAsync(id, version, http.User, ct);
            return result.Success ? Results.NoContent() : Failed(result);
        });

        // ---- Daily work plan -------------------------------------------------------------------

        api.MapPost("/plan/rows", async (HttpContext http, PlanningService planning, PlanRowRequest request, CancellationToken ct) =>
        {
            var result = await planning.CreateOrderAsync(request.ToDraft(), http.User, ct);
            if (!result.Success) return Failed(result);
            return Results.Created($"/api/plan/rows/{result.Id}", await planning.Reader.GetOrderAsync(result.Id!.Value, ct));
        });

        api.MapPut("/plan/rows/{id:int}", async (HttpContext http, PlanningService planning, int id, PlanRowRequest request, CancellationToken ct) =>
        {
            if (request.Version is not { } version) return VersionRequired();
            var result = await planning.UpdateOrderAsync(id, version, request.ToDraft(), http.User, ct);
            if (!result.Success) return Failed(result);
            return Results.Ok(await planning.Reader.GetOrderAsync(id, ct));
        });

        api.MapPost("/plan/rows/{id:int}/cancel", async (HttpContext http, PlanningService planning, int id, CancellationToken ct) =>
        {
            var result = await planning.CancelOrderAsync(id, http.User, ct);
            if (!result.Success) return Failed(result);
            return Results.Ok(await planning.Reader.GetOrderAsync(id, ct));
        });

        api.MapPost("/plan/rows/{id:int}/move", async (HttpContext http, PlanningService planning, int id, MoveRequest request, CancellationToken ct) =>
        {
            var direction = request.Direction?.Trim().ToLowerInvariant() switch
            {
                "up" => -1,
                "down" => 1,
                _ => 0,
            };
            if (direction == 0)
                return Results.Problem("La direzione deve essere \"up\" o \"down\".", statusCode: StatusCodes.Status400BadRequest);
            var result = await planning.MoveOrderAsync(id, direction, http.User, ct);
            if (!result.Success) return Failed(result);
            var row = await planning.Reader.GetOrderAsync(id, ct);
            return row is null ? Results.NotFound() : Results.Ok(await PlanDto.LoadAsync(planning.Reader, row.WorkDate, ct));
        });

        api.MapPost("/plan/copy-missing", async (HttpContext http, PlanningService planning, CopyMissingRequest request, CancellationToken ct) =>
        {
            var to = request.To ?? planning.CopyTargetFor(request.From);
            var result = await planning.CopyMissingAsync(request.From, to, http.User, ct);
            if (!result.Success) return Failed(result);
            return Results.Ok(await PlanDto.LoadAsync(planning.Reader, to, ct));
        });

        return endpoints;
    }

    /// <summary>Protects every endpoint of <paramref name="group"/> with the API key (see <see cref="AuthenticateAsync"/>).</summary>
    public static RouteGroupBuilder RequireApiKey(this RouteGroupBuilder group, ApiKeyOptions options) =>
        group.AddEndpointFilter((context, next) => AuthenticateAsync(context, next, options));

    /// <summary>Accepts the call only with the configured key; the caller then acts as <see cref="ApiKeyOptions.ClientName"/>.</summary>
    private static async ValueTask<object?> AuthenticateAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next, ApiKeyOptions options)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[ApiKeyOptions.HeaderName].ToString();
        if (!KeyMatches(key, options.Key))
        {
            var events = http.RequestServices.GetRequiredService<EventLog>();
            var time = http.RequestServices.GetRequiredService<TimeProvider>();
            var remote = http.Connection.RemoteIpAddress?.ToString() ?? "?";
            events.Add(new RobotEvent(time.GetUtcNow(), EventLevel.Warning, "Api",
                $"{http.Request.Method} {http.Request.Path} rifiutato da {remote}: API key {(key.Length == 0 ? "mancante" : "errata")}."));
            return Results.Problem(
                options.Key.Length == 0 ? "L'API di scrittura è disattivata: nessuna API key configurata." : $"API key mancante o errata (header {ApiKeyOptions.HeaderName}).",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // Read by the handlers through HttpContext.User (parameters are bound before this filter runs).
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, options.ClientName), new Claim(ClaimTypes.Role, Policies.Planner)],
            AuthenticationType));
        return await next(context);
    }

    /// <summary>Constant-time comparison; an empty configured key never matches.</summary>
    public static bool KeyMatches(string supplied, string configured) =>
        configured.Length > 0
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(configured));

    private static IResult Failed(EditResult result) => Results.Problem(
        string.Join(" ", result.Errors),
        statusCode: result.Conflict ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest,
        extensions: new Dictionary<string, object?> { ["errors"] = result.Errors });

    private static IResult VersionRequired() => Results.Problem(
        "\"version\" è obbligatorio: invia la versione letta con GET, così una modifica concorrente non viene sovrascritta.",
        statusCode: StatusCodes.Status400BadRequest);
}

/// <summary>Body of POST /api/jobs and PUT /api/jobs/{id} (PUT also needs <see cref="Version"/>).</summary>
public sealed record JobRequest(
    string? ProductCode,
    string? PlanName,
    string? Description = null,
    bool Enabled = true,
    bool IsService = false,
    int? Version = null)
{
    public JobDraft ToDraft() => new(ProductCode ?? "", PlanName ?? "", Description, Enabled, IsService);
}

/// <summary>
/// Body of POST /api/plan/rows and PUT /api/plan/rows/{id} (PUT also needs <see cref="Version"/>).
/// A missing <see cref="Date"/> means today.
/// </summary>
public sealed record PlanRowRequest(
    string? ProductCode,
    int Quantity,
    DateOnly? Date = null,
    string? OrderNumber = null,
    string? Notes = null,
    int? Version = null)
{
    public OrderDraft ToDraft() => new(OrderNumber ?? "", ProductCode ?? "", Quantity, Notes, Date);
}

/// <summary>Body of POST /api/plan/rows/{id}/move: "up" or "down" inside the same day.</summary>
public sealed record MoveRequest(string? Direction);

/// <summary>
/// Body of POST /api/plan/copy-missing. Without <see cref="To"/> the target is the day after
/// <see cref="From"/>, or today when that day is already past.
/// </summary>
public sealed record CopyMissingRequest(DateOnly From, DateOnly? To = null);
