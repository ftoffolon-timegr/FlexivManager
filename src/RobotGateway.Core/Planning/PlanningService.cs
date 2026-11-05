using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace RobotGateway.Core;

public sealed class PlanningOptions
{
    /// <summary>When true, edits from an unauthenticated user are refused (defense in depth behind the login).</summary>
    public bool RequireAuthenticatedUser { get; set; } = true;

    /// <summary>
    /// A job (not a service code) may only use an article code received from the ERP (Erp:OnlyImportedCodes).
    /// </summary>
    public bool OnlyImportedCodes { get; set; } = true;
}

public sealed record EditResult(bool Success, IReadOnlyList<string> Errors)
{
    /// <summary>Id of the created job or plan row, when the edit created one.</summary>
    public int? Id { get; init; }

    /// <summary>Failed because the data changed or has the wrong status (not because of invalid input).</summary>
    public bool Conflict { get; init; }

    public static EditResult Ok { get; } = new(true, []);
    public static EditResult Created(int id) => new(true, []) { Id = id };
    public static EditResult Fail(params string[] errors) => new(false, errors);
}

/// <summary>
/// Remote configuration: jobs (code -> plan) and the daily work plan (rows: day, code, quantity).
/// Registered on the monitoring host only. It never talks to the robot: the robot plan list comes from
/// the snapshot published by the worker.
/// </summary>
public sealed class PlanningService(
    IPlanningStore store,
    SnapshotStore snapshots,
    IOptions<PlanningOptions> planningOptions,
    EventLog events,
    TimeProvider time,
    IErpStore erp)
{
    /// <summary>Robot plan list as last read by the worker.</summary>
    public IReadOnlyList<string> RobotPlans => snapshots.Current.AvailablePlans;

    public IPlanningReader Reader => store;

    public DateOnly Today => WorkDay.Today(time);

    // ---- Jobs --------------------------------------------------------------------------------

    public Task<EditResult> CreateJobAsync(JobDraft draft, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "creazione codice", async name =>
        {
            var d = PlanningValidator.Normalize(draft);
            var errors = PlanningValidator.Validate(d, RobotPlans.ToArray()).ToList();
            if (await CheckImportedCodeAsync(d, unchangedCode: null, ct) is { } codeError) errors.Add(codeError);
            if (errors.Count > 0) return new EditResult(false, errors);
            var job = await store.CreateJobAsync(d, name, ct);
            Log(EventLevel.Info, $"Codice {job.Describe()} creato da {name}.");
            return EditResult.Created(job.Id);
        });

    public Task<EditResult> UpdateJobAsync(int id, int expectedVersion, JobDraft draft, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "modifica codice", async name =>
        {
            var d = PlanningValidator.Normalize(draft);
            var errors = PlanningValidator.Validate(d, RobotPlans.ToArray()).ToList();
            var current = await store.GetJobAsync(id, ct);
            if (await CheckImportedCodeAsync(d, current?.ProductCode, ct) is { } codeError) errors.Add(codeError);
            if (errors.Count > 0) return new EditResult(false, errors);
            var job = await store.UpdateJobAsync(id, expectedVersion, d, name, ct);
            Log(EventLevel.Info, $"Codice {job.Describe()} modificato da {name}.");
            return EditResult.Ok;
        });

    public Task<EditResult> DeleteJobAsync(int id, int expectedVersion, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "eliminazione codice", async name =>
        {
            var job = await store.GetJobAsync(id, ct);
            await store.DeleteJobAsync(id, expectedVersion, name, ct);
            Log(EventLevel.Info, $"Codice {job?.Describe() ?? id.ToString()} eliminato da {name}.");
            return EditResult.Ok;
        });

    /// <summary>Whether jobs are restricted to the article codes received from the ERP.</summary>
    public bool OnlyImportedCodes => planningOptions.Value.OnlyImportedCodes;

    /// <summary>
    /// Article codes from the ERP that have no job yet, plus <paramref name="keep"/> (the code of the job being
    /// edited): the choices offered by the code editor.
    /// </summary>
    public async Task<IReadOnlyList<ArticleCode>> ListAssignableArticlesAsync(string? keep = null, CancellationToken ct = default)
    {
        var used = (await store.ListJobsAsync(ct)).Select(j => j.ProductCode).ToHashSet(StringComparer.Ordinal);
        if (keep is not null) used.Remove(keep);
        return (await erp.ListArticlesAsync(ct)).Where(a => !used.Contains(a.Code)).ToList();
    }

    /// <summary>
    /// Null when the code may be used: a service code, a code received from the ERP, or the job's current code
    /// (jobs created before the ERP interface keep working).
    /// </summary>
    private async Task<string?> CheckImportedCodeAsync(JobDraft d, string? unchangedCode, CancellationToken ct)
    {
        if (!OnlyImportedCodes || d.IsService || d.ProductCode.Length == 0 || d.ProductCode == unchangedCode)
            return null;
        var imported = (await erp.ListArticlesAsync(ct)).Any(a => a.Code == d.ProductCode);
        return imported ? null : $"Il codice '{d.ProductCode}' non è tra i codici articolo ricevuti dal gestionale.";
    }

    // ---- Daily work plan ---------------------------------------------------------------------

    /// <summary>Progress of the plan of <paramref name="date"/>: done / in progress / missing.</summary>
    public async Task<DailyReport> GetReportAsync(DateOnly date, CancellationToken ct = default) =>
        DailyReport.Build(date, await store.ListPlanAsync(date, ct));

    public Task<EditResult> CreateOrderAsync(OrderDraft draft, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "creazione ordine", async name =>
        {
            var d = Normalize(draft);
            if (await ValidateOrder(d, ct) is { } invalid) return invalid;
            var order = await store.CreateOrderAsync(d, name, ct);
            Log(EventLevel.Info, $"Ordini di produzione: {order.Describe()} aggiunto da {name}.");
            return EditResult.Created(order.Id);
        });

    public Task<EditResult> UpdateOrderAsync(int id, int expectedVersion, OrderDraft draft, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "modifica ordine", async name =>
        {
            var d = Normalize(draft);
            if (await ValidateOrder(d, ct) is { } invalid) return invalid;
            var order = await store.UpdateOrderAsync(id, expectedVersion, d, name, ct);
            Log(EventLevel.Info, $"Ordini di produzione: {order.Describe()} modificato da {name}.");
            return EditResult.Ok;
        });

    /// <summary>Default target of "copy missing": the next day, or today when the next day is already past.</summary>
    public DateOnly CopyTargetFor(DateOnly from) =>
        from.AddDays(1) < Today ? Today : from.AddDays(1);

    /// <summary>Copies what is still missing on <paramref name="from"/> into the plan of <paramref name="to"/>.</summary>
    public Task<EditResult> CopyMissingAsync(DateOnly from, DateOnly to, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "copia mancanti", async name =>
        {
            if (to <= from)
                return EditResult.Fail("Il giorno di destinazione deve essere successivo a quello di origine.");
            if (to < Today)
                return EditResult.Fail("Il giorno di destinazione è nel passato.");
            var created = await store.CopyMissingAsync(from, to, name, ct);
            Log(EventLevel.Info, $"Ordini di produzione: {created.Count} ordini con pezzi mancanti del {from:dd/MM/yyyy} copiati al {to:dd/MM/yyyy} da {name}.");
            return EditResult.Ok;
        });

    public Task<EditResult> MoveOrderAsync(int id, int direction, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "spostamento ordine", async name =>
        {
            await store.MoveOrderAsync(id, Math.Sign(direction), name, ct);
            var order = await store.GetOrderAsync(id, ct);
            Log(EventLevel.Info, $"Ordini di produzione: {order?.Describe() ?? id.ToString()} spostato {(direction < 0 ? "su" : "giù")} da {name}.");
            return EditResult.Ok;
        });

    public Task<EditResult> CancelOrderAsync(int id, ClaimsPrincipal user, CancellationToken ct = default) =>
        Edit(user, "annullamento ordine", async name =>
        {
            var order = await store.CancelOrderAsync(id, name, ct);
            Log(EventLevel.Info, $"Ordini di produzione: {order.Describe()} annullato da {name}.");
            return EditResult.Ok;
        });

    /// <summary>Normalized draft with its day resolved (null = today).</summary>
    private OrderDraft Normalize(OrderDraft draft) =>
        PlanningValidator.Normalize(draft) with { WorkDate = draft.WorkDate ?? Today };

    private async Task<EditResult?> ValidateOrder(OrderDraft d, CancellationToken ct)
    {
        var errors = PlanningValidator.Validate(d).ToList();
        if (d.WorkDate < Today)
            errors.Add("Il giorno dell'ordine è nel passato.");
        if (errors.Count == 0)
        {
            var job = await store.FindJobByCodeAsync(d.ProductCode, ct);
            if (job is null)
                errors.Add($"Il codice '{d.ProductCode}' non ha un plan associato: crea prima l'associazione.");
            else if (job.IsService)
                errors.Add($"'{d.ProductCode}' è un codice di servizio: è sempre disponibile e non si pianifica.");
        }
        return errors.Count == 0 ? null : new EditResult(false, errors);
    }

    private async Task<EditResult> Edit(ClaimsPrincipal user, string action, Func<string, Task<EditResult>> edit)
    {
        if (planningOptions.Value.RequireAuthenticatedUser && user.Identity?.IsAuthenticated != true)
        {
            Log(EventLevel.Warning, $"Pianificazione: {action} rifiutata, utente non autenticato.");
            return EditResult.Fail("Serve l'accesso.");
        }
        try
        {
            return await edit(UserName(user));
        }
        catch (PlanningConflictException ex)
        {
            return EditResult.Fail(ex.Message) with { Conflict = true };
        }
    }

    private static string UserName(ClaimsPrincipal user) =>
        user.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : "anonymous";

    private void Log(EventLevel level, string message) =>
        events.Add(new RobotEvent(time.GetUtcNow(), level, "Planning", message));
}
