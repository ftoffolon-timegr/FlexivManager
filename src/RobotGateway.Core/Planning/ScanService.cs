namespace RobotGateway.Core;

/// <summary>What a scanned or typed code selects. Never starts motion.</summary>
/// <param name="Code">Normalized code.</param>
/// <param name="Job">The job of the code; null when the code cannot be run now.</param>
/// <param name="Order">The open row of today's plan with this code; null only for a service job.</param>
public sealed record ScanResult(string Code, Job? Job, ProductionOrder? Order, string? Error)
{
    public bool IsValid => Job is not null;
}

/// <summary>
/// Resolves product codes read by the barcode scanner (operator host). Only codes of today's work plan are
/// accepted, plus service jobs (e.g. HOME). Every scan is logged.
/// </summary>
public sealed class ScanService(IPlanningReader reader, EventLog events, TimeProvider time)
{
    public async Task<ScanResult> ResolveAsync(string raw, string source, CancellationToken ct = default)
    {
        var code = ProductCode.Normalize(raw);
        if (ProductCode.Validate(code) is { } invalid)
            return Done(new ScanResult(code, null, null, invalid), source);

        var job = await reader.FindJobByCodeAsync(code, ct);
        if (job is null)
            return Done(new ScanResult(code, null, null, $"Codice '{code}' sconosciuto."), source);
        if (!job.Enabled)
            return Done(new ScanResult(code, null, null, $"Il codice '{code}' è disattivato."), source);
        if (job.IsService)
            return Done(new ScanResult(code, job, null, null), source);

        var today = WorkDay.Today(time);
        var row = (await reader.ListPlanAsync(today, ct)).FirstOrDefault(o => o.IsOpen && o.ProductCode == code);
        return row is null
            ? Done(new ScanResult(code, null, null, $"'{code}' non è negli ordini di produzione di oggi (o è già completato)."), source)
            : Done(new ScanResult(code, job, row, null), source);
    }

    /// <summary>Records a code read while it could not be accepted (e.g. a cycle is running).</summary>
    public void LogIgnored(string raw, string reason, string source) =>
        Log(EventLevel.Warning, $"Lettura '{ProductCode.Normalize(raw)}' da {source} ignorata: {reason}");

    private ScanResult Done(ScanResult result, string source)
    {
        if (!result.IsValid)
            Log(EventLevel.Warning, $"Lettura '{result.Code}' da {source}: {result.Error}");
        else if (result.Order is { } order)
            Log(EventLevel.Info, $"Lettura '{result.Code}' da {source}: selezionato {order.Describe()}, codice {result.Job!.Describe()}.");
        else
            Log(EventLevel.Info, $"Lettura '{result.Code}' da {source}: codice di servizio {result.Job!.Describe()}.");
        return result;
    }

    private void Log(EventLevel level, string message) =>
        events.Add(new RobotEvent(time.GetUtcNow(), level, "Scanner", message));
}
