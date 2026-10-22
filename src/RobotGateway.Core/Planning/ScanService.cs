namespace RobotGateway.Core;

/// <summary>What a scanned or typed code selects. Never starts motion.</summary>
/// <param name="Code">Normalized code.</param>
/// <param name="Job">The job of the code; null when unknown, disabled or invalid.</param>
/// <param name="Order">First open order with this code, if any; otherwise the job runs as a one-off.</param>
public sealed record ScanResult(string Code, Job? Job, ProductionOrder? Order, string? Error)
{
    public bool IsValid => Job is not null;
}

/// <summary>Resolves product codes read by the barcode scanner (operator host). Every scan is logged.</summary>
public sealed class ScanService(IPlanningReader reader, EventLog events, TimeProvider time)
{
    public async Task<ScanResult> ResolveAsync(string raw, string source, CancellationToken ct = default)
    {
        var code = ProductCode.Normalize(raw);
        if (ProductCode.Validate(code) is { } invalid)
            return Done(new ScanResult(code, null, null, invalid), source);

        var job = await reader.FindJobByCodeAsync(code, ct);
        if (job is null)
            return Done(new ScanResult(code, null, null, $"Unknown product code '{code}'."), source);
        if (!job.Enabled)
            return Done(new ScanResult(code, null, null, $"Job for '{code}' is disabled."), source);

        var order = (await reader.ListOpenOrdersAsync(ct)).FirstOrDefault(o => o.ProductCode == code);
        return Done(new ScanResult(code, job, order, null), source);
    }

    /// <summary>Records a code read while it could not be accepted (e.g. a cycle is running).</summary>
    public void LogIgnored(string raw, string reason, string source) =>
        Log(EventLevel.Warning, $"Scan '{ProductCode.Normalize(raw)}' from {source} ignored: {reason}");

    private ScanResult Done(ScanResult result, string source)
    {
        if (!result.IsValid)
            Log(EventLevel.Warning, $"Scan '{result.Code}' from {source}: {result.Error}");
        else if (result.Order is { } order)
            Log(EventLevel.Info, $"Scan '{result.Code}' from {source}: selected {order.Describe()}, job {result.Job!.Describe()}.");
        else
            Log(EventLevel.Info, $"Scan '{result.Code}' from {source}: no open order, one-off job {result.Job!.Describe()}.");
        return result;
    }

    private void Log(EventLevel level, string message) =>
        events.Add(new RobotEvent(time.GetUtcNow(), level, "Scanner", message));
}
