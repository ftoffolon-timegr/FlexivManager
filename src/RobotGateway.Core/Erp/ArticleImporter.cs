using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RobotGateway.Core;

/// <summary>Outcome of one file.</summary>
/// <param name="Deleted">True when the file was removed (the ERP's "codes loaded" signal).</param>
/// <param name="Error">Why the file was not imported or not deleted; null when everything went fine.</param>
public sealed record ArticleFileResult(
    string FileName,
    int Codes,
    int Added,
    int AlreadyKnown,
    IReadOnlyList<RejectedLine> Rejected,
    bool Deleted,
    string? Error);

/// <summary>Outcome of one scan of the import folder.</summary>
/// <param name="Trigger">"automatica" (poller) or who asked for it (API client, planner).</param>
/// <param name="Error">Folder-level problem (not configured, unreachable); null otherwise.</param>
/// <param name="Skipped">Files left for the next scan: still being written or locked by the ERP.</param>
public sealed record ArticleScanResult(
    DateTimeOffset At,
    string Trigger,
    string Folder,
    IReadOnlyList<ArticleFileResult> Files,
    IReadOnlyList<string> Skipped,
    string? Error)
{
    public int CodesAdded => Files.Sum(f => f.Added);
    public bool Success => Error is null && Files.All(f => f.Error is null);
}

/// <summary>
/// Reads the ERP article files of the import folder, stores their codes and deletes each file once stored.
/// A file is deleted only after its codes are in the database: if anything fails, it stays in the folder and is
/// read again at the next scan (importing a code twice is harmless). One scan at a time.
/// </summary>
public sealed class ArticleImporter(
    ErpOptions options,
    string importFolder,
    IErpStore store,
    EventLog events,
    TimeProvider time,
    ILogger<ArticleImporter> logger)
{
    private readonly SemaphoreSlim _scan = new(1, 1);
    private string? _lastFolderError;

    public ErpOptions Options => options;

    /// <summary>Absolute import folder (empty when not configured).</summary>
    public string Folder => importFolder;

    /// <summary>Last scan, automatic or manual; null before the first one.</summary>
    public ArticleScanResult? LastScan { get; private set; }

    /// <param name="manual">
    /// Requested by a person or by the ERP (API): files are read even if just written, relying only on the
    /// exclusive open to skip a file still being written. The automatic scan also waits for <see cref="ErpOptions.MinFileAge"/>.
    /// </param>
    public async Task<ArticleScanResult> ScanAsync(string trigger, bool manual = true, CancellationToken ct = default)
    {
        await _scan.WaitAsync(ct);
        try
        {
            var result = await ScanCoreAsync(trigger, manual ? TimeSpan.Zero : options.MinFileAge, ct);
            LastScan = result;
            return result;
        }
        finally
        {
            _scan.Release();
        }
    }

    private async Task<ArticleScanResult> ScanCoreAsync(string trigger, TimeSpan minFileAge, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (importFolder.Length == 0)
            return new ArticleScanResult(now, trigger, "", [], [], "Cartella di importazione non configurata (Erp:ImportFolder).");

        FileInfo[] files;
        try
        {
            var dir = new DirectoryInfo(importFolder);
            if (!dir.Exists)
                throw new DirectoryNotFoundException($"La cartella {importFolder} non esiste o non è raggiungibile.");
            files = dir.GetFiles(options.FilePattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            var message = ex is DirectoryNotFoundException ? ex.Message : $"Cartella {importFolder} non leggibile: {ex.Message}";
            // Logged once per problem, not at every poll.
            if (message != _lastFolderError)
                Log(EventLevel.Error, $"Importazione codici: {message}");
            _lastFolderError = message;
            return new ArticleScanResult(now, trigger, importFolder, [], [], message);
        }
        if (_lastFolderError is not null)
            Log(EventLevel.Info, $"Importazione codici: cartella {importFolder} di nuovo raggiungibile.");
        _lastFolderError = null;

        var results = new List<ArticleFileResult>();
        var skipped = new List<string>();
        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (now - new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) < minFileAge)
            {
                skipped.Add(file.Name);
                continue;
            }
            var result = await ImportFileAsync(file, trigger, ct);
            if (result is null) skipped.Add(file.Name);
            else results.Add(result);
        }
        return new ArticleScanResult(now, trigger, importFolder, results, skipped, null);
    }

    /// <returns>Null when the file is locked (the ERP is still writing it): it is retried at the next scan.</returns>
    private async Task<ArticleFileResult?> ImportFileAsync(FileInfo file, string trigger, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            // Exclusive open: fails while the ERP still has the file open for writing.
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            return null; // removed meanwhile
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "ERP file {File} locked, retried at the next scan", file.Name);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failed(file, $"File {file.Name} non leggibile: {ex.Message}");
        }

        var parsed = ArticleFile.Parse(ArticleFile.Decode(bytes));
        ArticleImportCounts counts;
        try
        {
            counts = await store.ImportArticlesAsync(parsed.Codes, file.Name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Storing the codes of {File} failed", file.Name);
            return Failed(file, $"Codici di {file.Name} non salvati, il file resta nella cartella: {ex.Message}");
        }

        var deleted = false;
        string? error = null;
        if (options.DeleteAfterImport)
        {
            try
            {
                File.Delete(file.FullName);
                deleted = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"Codici di {file.Name} salvati ma file non cancellato (il gestionale non riceve la conferma): {ex.Message}";
                Log(EventLevel.Error, $"Importazione codici: {error}");
            }
        }

        var summary = $"Importazione codici ({trigger}): {file.Name}, {parsed.Codes.Count} codici, {counts.Added} nuovi, {counts.AlreadyKnown} già presenti"
            + (parsed.Rejected.Count > 0 ? $", {parsed.Rejected.Count} righe scartate" : "")
            + (deleted ? ", file cancellato." : ".");
        Log(parsed.Rejected.Count > 0 ? EventLevel.Warning : EventLevel.Info, summary);
        foreach (var line in parsed.Rejected.Take(5))
            Log(EventLevel.Warning, $"Importazione codici: {file.Name} riga {line.LineNumber} scartata ('{line.Text}'): {line.Reason}");

        return new ArticleFileResult(file.Name, parsed.Codes.Count, counts.Added, counts.AlreadyKnown, parsed.Rejected, deleted, error);
    }

    private ArticleFileResult Failed(FileInfo file, string error)
    {
        Log(EventLevel.Error, $"Importazione codici: {error}");
        return new ArticleFileResult(file.Name, 0, 0, 0, [], false, error);
    }

    private void Log(EventLevel level, string message) =>
        events.Add(new RobotEvent(time.GetUtcNow(), level, "Erp", message));
}

/// <summary>Scans the ERP import folder every <see cref="ErpOptions.PollInterval"/>, starting at service start.</summary>
public sealed class ArticleImportService(ArticleImporter importer, TimeProvider time, ILogger<ArticleImportService> logger) : BackgroundService
{
    public const string AutomaticTrigger = "automatica";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = importer.Options;
        if (!options.Enabled || importer.Folder.Length == 0)
        {
            logger.LogInformation("ERP article import disabled (Erp:Enabled / Erp:ImportFolder).");
            return;
        }
        logger.LogInformation("ERP article import: scanning {Folder} every {Interval}", importer.Folder, options.PollInterval);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await importer.ScanAsync(AutomaticTrigger, manual: false, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "ERP article scan failed");
                }
                await Task.Delay(options.PollInterval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }
}
