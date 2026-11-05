using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RobotGateway.Core;

namespace RobotGateway.Tests;

public sealed class ArticleFileTests
{
    [Fact]
    public void Parse_OneCodePerLine_SkipsBlanks_MergesDuplicates_KeepsTheErpSpelling()
    {
        var file = ArticleFile.Parse("A100\r\n  b-200 \r\n\r\n\ta100\nX/7.5\n");

        Assert.Equal(["A100", "B-200", "X/7.5"], file.Codes.Select(c => c.Code));
        Assert.Equal(["A100", "b-200", "X/7.5"], file.Codes.Select(c => c.RawCode));
        Assert.Empty(file.Rejected);
    }

    [Fact]
    public void Parse_RejectsLinesThatCannotBeACode()
    {
        var file = ArticleFile.Parse($"OK1\n{new string('9', ProductCode.MaxLength + 1)}\nCODICE-È\n");

        Assert.Equal("OK1", Assert.Single(file.Codes).Code);
        Assert.Equal([2, 3], file.Rejected.Select(r => r.LineNumber));
    }

    [Fact]
    public void Decode_HandlesBomAndLatin1()
    {
        Assert.Equal("A1", ArticleFile.Decode([0xEF, 0xBB, 0xBF, (byte)'A', (byte)'1']));
        Assert.Equal("A1", ArticleFile.Decode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("A1")).ToArray()));
        Assert.Equal("È", ArticleFile.Decode([0xC8])); // not valid UTF-8 -> Latin-1
    }
}

public sealed class ArticleImporterTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly TempPlanningDb _db;
    private readonly string _dir = Directory.CreateTempSubdirectory("erp-test-").FullName;
    private readonly EventLog _events = new();

    public ArticleImporterTests()
    {
        _db = new TempPlanningDb(_time);
        _time.Advance(TimeSpan.FromDays(400)); // files written "now" by the real clock are older than MinFileAge
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ArticleImporter Importer(string? folder = null, ErpOptions? options = null) =>
        new(options ?? new ErpOptions { MinFileAge = TimeSpan.Zero }, folder ?? _dir, _db.Store, _events, _time, NullLogger<ArticleImporter>.Instance);

    private string WriteFile(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task Scan_StoresTheCodes_ThenDeletesTheFile()
    {
        var path = WriteFile("a.txt", "A1\r\nA2\r\n");

        var result = await Importer().ScanAsync("test");

        Assert.True(result.Success);
        var file = Assert.Single(result.Files);
        Assert.Equal((2, 2, 0, true), (file.Codes, file.Added, file.AlreadyKnown, file.Deleted));
        Assert.False(File.Exists(path));
        Assert.Equal(["A1", "A2"], (await _db.Store.ListArticlesAsync()).Select(a => a.Code));
    }

    [Fact]
    public async Task Scan_KnownCodesAreRefreshed_NotDuplicated_AndOldCodesStay()
    {
        var importer = Importer();
        WriteFile("1.txt", "A1\nA2\n");
        await importer.ScanAsync("test");
        _time.Advance(TimeSpan.FromHours(1));
        WriteFile("2.txt", "a2\nA3\n");

        var result = await importer.ScanAsync("test");

        Assert.Equal((1, 1), (result.Files[0].Added, result.Files[0].AlreadyKnown));
        var articles = await _db.Store.ListArticlesAsync();
        Assert.Equal(["A1", "A2", "A3"], articles.Select(a => a.Code));
        var a2 = articles.Single(a => a.Code == "A2");
        Assert.Equal(("a2", "2.txt"), (a2.RawCode, a2.SourceFile));
        Assert.True(a2.LastImportedAt > a2.FirstImportedAt);
    }

    [Fact]
    public async Task Scan_OnlyMatchingFiles_AndKeepsTheFileWhenDeletionIsOff()
    {
        WriteFile("other.csv", "X1\n");
        var path = WriteFile("a.txt", "A1\n");

        var result = await Importer(options: new ErpOptions { MinFileAge = TimeSpan.Zero, DeleteAfterImport = false }).ScanAsync("test");

        Assert.Equal("a.txt", Assert.Single(result.Files).FileName);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(_dir, "other.csv")));
    }

    [Fact]
    public async Task Scan_LeavesAFileStillBeingWritten()
    {
        var path = WriteFile("a.txt", "A1\n");
        var options = new ErpOptions { MinFileAge = TimeSpan.FromSeconds(30) };
        // Clock = file time: the file is younger than MinFileAge.
        var time = new ManualTimeProvider();
        time.Advance(File.GetLastWriteTimeUtc(path) - time.GetUtcNow().UtcDateTime);
        var importer = new ArticleImporter(options, _dir, _db.Store, _events, time, NullLogger<ArticleImporter>.Instance);

        var result = await importer.ScanAsync("test", manual: false);

        Assert.Empty(result.Files);
        Assert.Equal("a.txt", Assert.Single(result.Skipped));
        Assert.True(File.Exists(path));

        // A manual scan (page, API) reads it at once.
        Assert.Single((await importer.ScanAsync("test", manual: true)).Files);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Scan_MissingFolder_ReportsTheError_AndLogsItOnce()
    {
        var importer = Importer(Path.Combine(_dir, "missing"));

        var first = await importer.ScanAsync("test");
        await importer.ScanAsync("test");

        Assert.False(first.Success);
        Assert.NotNull(first.Error);
        Assert.Single(_events.GetRecent(), e => e.Level == EventLevel.Error);
    }

    [Fact]
    public async Task Scan_NotConfigured_ReportsTheError() =>
        Assert.NotNull((await Importer(folder: "").ScanAsync("test")).Error);
}

public sealed class ProductionExportTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test+1", TimeSpan.FromHours(1), "Test+1", "Test+1");
    private static readonly DateOnly Day = new(2026, 11, 5);

    /// <summary>Local time on <see cref="Day"/> in <see cref="Zone"/>.</summary>
    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 11, 5, hour, minute, 0, TimeSpan.FromHours(1));

    private static CycleRecord Cycle(string code, DateTimeOffset start, double seconds, CycleOutcome outcome = CycleOutcome.Completed, string plan = "PLAN-A") =>
        new(code, plan, null, null, "test", start, TimeSpan.FromSeconds(seconds), outcome, null);

    private static ProductionExport Build(
        IEnumerable<CycleRecord> cycles,
        IEnumerable<ProductionOrder>? plan = null,
        ActiveCycle? running = null,
        IEnumerable<Job>? jobs = null,
        IEnumerable<ArticleCode>? articles = null) =>
        ProductionExport.Build(Day, At(17), Zone, cycles, plan ?? [], running, jobs ?? [], articles ?? []);

    [Fact]
    public void FinishedCode_HasStartEndDurationAndQuantity()
    {
        var export = Build([
            Cycle("A1", At(8), 60.4),
            Cycle("A1", At(8, 5), 30, CycleOutcome.Stopped),
            Cycle("A1", At(8, 10), 59.8, plan: "PLAN-A2"),
        ]);

        var a = Assert.Single(export.Articles);
        Assert.Equal(ArticleProgress.Completed, a.Status);
        Assert.Equal(At(8), a.StartedAt);
        Assert.Equal(TimeSpan.FromHours(1), a.StartedAt.Offset);
        Assert.Equal(At(8, 10) + TimeSpan.FromSeconds(59.8), a.EndedAt);
        Assert.Equal(120, a.DurationSeconds); // completed cycles only
        Assert.Equal(2, a.Quantity);
        Assert.Equal("PLAN-A2", a.RobotProgram);
    }

    [Fact]
    public void RunningCode_HasOnlyTheStart()
    {
        var export = Build(
            [Cycle("A1", At(9), 60)],
            running: new ActiveCycle("A1", "PLAN-A", 1, null, "operator", At(9, 5)));

        var a = Assert.Single(export.Articles);
        Assert.Equal((ArticleProgress.InProgress, At(9)), (a.Status, a.StartedAt));
        Assert.Null(a.EndedAt);
        Assert.Null(a.DurationSeconds);
        Assert.Null(a.Quantity);
    }

    [Fact]
    public void FirstCycleRunning_IsListedInProgress()
    {
        var export = Build([], running: new ActiveCycle("B2", "PLAN-B", 1, null, "operator", At(10)));

        var a = Assert.Single(export.Articles);
        Assert.Equal(("B2", ArticleProgress.InProgress, "PLAN-B"), (a.ArticleCode, a.Status, a.RobotProgram));
    }

    [Fact]
    public void CodeWithAStartedOrderStillOpen_IsInProgressBetweenCycles()
    {
        var order = new ProductionOrder { Id = 1, WorkDate = Day, ProductCode = "A1", Quantity = 5, CompletedCount = 2, Status = OrderStatus.Active };

        var export = Build([Cycle("A1", At(8), 60), Cycle("A1", At(8, 2), 60)], plan: [order]);

        Assert.Equal(ArticleProgress.InProgress, Assert.Single(export.Articles).Status);
    }

    [Fact]
    public void ServiceCodes_AreLeftOut_AndTheErpSpellingIsUsed()
    {
        var jobs = new[] { new Job { ProductCode = "HOME", PlanName = "PLAN-Home", IsService = true } };
        var articles = new[] { new ArticleCode("AB-1", "ab-1", At(7), At(7), "codes.txt") };

        var export = Build([Cycle("HOME", At(7), 10), Cycle("AB-1", At(8), 10)], jobs: jobs, articles: articles);

        Assert.Equal("ab-1", Assert.Single(export.Articles).ArticleCode);
    }

    [Fact]
    public void ArticlesAreListedByStart_AndARunningCycleOfAnotherDayIsIgnored()
    {
        var export = Build(
            [Cycle("B", At(9), 10), Cycle("A", At(10), 10)],
            running: new ActiveCycle("C", "PLAN-C", null, null, "operator", At(10).AddDays(-1)));

        Assert.Equal(["B", "A"], export.Articles.Select(a => a.ArticleCode));
    }

    [Fact]
    public void DayBounds_AreLocalMidnights()
    {
        var (from, to) = ProductionExport.DayBounds(Day, Zone);

        Assert.Equal(new DateTimeOffset(2026, 11, 4, 23, 0, 0, TimeSpan.Zero), from.ToUniversalTime());
        Assert.Equal(TimeSpan.FromDays(1), to - from);
    }

    [Fact]
    public async Task Store_ListsTheCyclesStartedInTheWindow()
    {
        using var db = new TempPlanningDb();
        var (from, to) = ProductionExport.DayBounds(Day, Zone);
        foreach (var start in new[] { from.AddMinutes(-1), from, to.AddMinutes(-1), to })
            await db.Store.RecordCycleAsync(Cycle("A1", start, 10));

        var cycles = await db.Store.ListCyclesStartedBetweenAsync(from, to);

        Assert.Equal([from, to.AddMinutes(-1)], cycles.Select(c => c.StartedAt));
    }

    [Fact]
    public async Task Store_AddsTheArticlesTableToAnOlderDatabase()
    {
        using var db = new TempPlanningDb();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.Path}"))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE \"Articles\"";
            drop.ExecuteNonQuery();
        }

        db.Store.EnsureCreated();

        await db.Store.ImportArticlesAsync([new ParsedArticle("A1", "a1")], "f.txt");
        Assert.Single(await db.Store.ListArticlesAsync());
    }
}
