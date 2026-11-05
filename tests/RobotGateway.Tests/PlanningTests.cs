using System.Security.Claims;
using Microsoft.Extensions.Options;
using RobotGateway.Core;

namespace RobotGateway.Tests;

public class ProductCodeTests
{
    [Theory]
    [InlineData("a100", "A100")]
    [InlineData("  A100 \r\n", "A100")]       // keyboard-wedge suffix
    [InlineData("\u001DA100\t", "A100")]       // GS1 group separator / tab
    [InlineData("ab-12/x.y", "AB-12/X.Y")]
    public void Normalize_TrimsStripsControlCharsAndUpperCases(string raw, string expected) =>
        Assert.Equal(expected, ProductCode.Normalize(raw));

    [Fact]
    public void Validate_RejectsEmptyLongAndNonAscii()
    {
        Assert.NotNull(ProductCode.Validate(ProductCode.Normalize("   ")));
        Assert.NotNull(ProductCode.Validate(new string('A', ProductCode.MaxLength + 1)));
        Assert.NotNull(ProductCode.Validate("ÀBC"));
        Assert.Null(ProductCode.Validate("A100"));
    }
}

public class PlanningValidatorTests
{
    private static readonly string[] RobotPlans = ["PLAN-A", "PLAN-B"];

    [Fact]
    public void ValidJob_HasNoErrors() =>
        Assert.Empty(PlanningValidator.Validate(new JobDraft("A1", "PLAN-A", null, true), RobotPlans));

    [Fact]
    public void Job_PlanMustBeOnRobot()
    {
        var errors = PlanningValidator.Validate(new JobDraft("A1", "PLAN-X", null, true), RobotPlans);
        Assert.Single(errors);
    }

    [Fact]
    public void Job_PlanNotCheckedWhenRobotListUnknown() =>
        Assert.Empty(PlanningValidator.Validate(new JobDraft("A1", "PLAN-X", null, true), []));

    [Fact]
    public void Order_RequiresCodeAndPositiveQuantity()
    {
        Assert.Equal(2, PlanningValidator.Validate(new OrderDraft("", "", 0, null)).Count); // order number is optional
        Assert.Empty(PlanningValidator.Validate(PlanningValidator.Normalize(new OrderDraft(" OP-1 ", "a1", 5, " "))));
    }
}

public class PlanningServiceTests : IDisposable
{
    private readonly TempPlanningDb _db = new();
    private readonly SnapshotStore _snapshots = new();
    private readonly EventLog _events = new();

    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());
    private static readonly ClaimsPrincipal Planner = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "planner")], "test"));

    public PlanningServiceTests() =>
        _snapshots.Publish(new RobotSnapshot { Sequence = 1, AvailablePlans = ["PLAN-A", "PLAN-B"] });

    public void Dispose() => _db.Dispose();

    private PlanningService Service(bool requireLogin = true, bool onlyImported = false) => new(
        _db.Store, _snapshots,
        Options.Create(new PlanningOptions { RequireAuthenticatedUser = requireLogin, OnlyImportedCodes = onlyImported }),
        _events, TimeProvider.System, _db.Store);

    // ---- Codes restricted to the ERP list ----------------------------------------------------

    [Fact]
    public async Task OnlyImportedCodes_RefusesACodeNotReceivedFromTheErp()
    {
        var result = await Service(onlyImported: true).CreateJobAsync(new JobDraft("X9", "PLAN-A", null, true), Planner);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("gestionale"));
        Assert.Empty(await _db.Store.ListJobsAsync());
    }

    [Fact]
    public async Task OnlyImportedCodes_AcceptsAnImportedCode_InAnyCase()
    {
        await _db.Store.ImportArticlesAsync([new ParsedArticle("AB-1", "ab-1")], "f.txt");

        Assert.True((await Service(onlyImported: true).CreateJobAsync(new JobDraft("ab-1", "PLAN-A", null, true), Planner)).Success);
    }

    [Fact]
    public async Task OnlyImportedCodes_ServiceCodesAreFree()
    {
        var result = await Service(onlyImported: true).CreateJobAsync(new JobDraft("HOME", "PLAN-B", null, true, IsService: true), Planner);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task OnlyImportedCodes_AnOlderJobKeepsItsCode_ButCannotMoveToAnotherFreeCode()
    {
        var created = await Service().CreateJobAsync(new JobDraft("OLD1", "PLAN-A", null, true), Planner);
        var job = (await _db.Store.GetJobAsync(created.Id!.Value))!;
        var service = Service(onlyImported: true);

        Assert.True((await service.UpdateJobAsync(job.Id, job.Version, new JobDraft("OLD1", "PLAN-B", null, true), Planner)).Success);
        job = (await _db.Store.GetJobAsync(job.Id))!;
        Assert.False((await service.UpdateJobAsync(job.Id, job.Version, new JobDraft("NEW1", "PLAN-B", null, true), Planner)).Success);
    }

    [Fact]
    public async Task AssignableArticles_AreTheImportedCodesWithoutAJob_PlusTheEditedOne()
    {
        await _db.Store.ImportArticlesAsync([new ParsedArticle("A1", "a1"), new ParsedArticle("A2", "A2"), new ParsedArticle("A3", "A3")], "f.txt");
        var service = Service(onlyImported: true);
        await service.CreateJobAsync(new JobDraft("A1", "PLAN-A", null, true), Planner);

        Assert.Equal(["A2", "A3"], (await service.ListAssignableArticlesAsync()).Select(a => a.Code));
        Assert.Equal(["A1", "A2", "A3"], (await service.ListAssignableArticlesAsync(keep: "A1")).Select(a => a.Code));
    }

    [Fact]
    public async Task Edits_RequireAuthenticatedUser()
    {
        var result = await Service().CreateJobAsync(new JobDraft("A1", "PLAN-A", null, true), Anonymous);

        Assert.False(result.Success);
        Assert.Empty(await _db.Store.ListJobsAsync());
    }

    [Fact]
    public async Task Edits_AllowedWithoutLogin_WhenDisabled()
    {
        var result = await Service(requireLogin: false).CreateJobAsync(new JobDraft("a1", "PLAN-A", null, true), Anonymous);

        Assert.True(result.Success);
        var job = Assert.Single(await _db.Store.ListJobsAsync());
        Assert.Equal("A1", job.ProductCode);
        Assert.Equal("anonymous", job.UpdatedBy);
    }

    [Fact]
    public async Task CreateJob_ValidatesAndLogsWho()
    {
        var service = Service();
        Assert.False((await service.CreateJobAsync(new JobDraft("A1", "PLAN-X", null, true), Planner)).Success);
        Assert.True((await service.CreateJobAsync(new JobDraft("A1", "PLAN-A", null, true), Planner)).Success);
        Assert.False((await service.CreateJobAsync(new JobDraft("a1", "PLAN-B", null, true), Planner)).Success); // duplicate code

        Assert.Contains(_events.GetRecent(), e => e.Message.Contains("creato da planner"));
    }

    [Fact]
    public async Task SeveralCodes_MayUseTheSamePlan()
    {
        var service = Service();
        Assert.True((await service.CreateJobAsync(new JobDraft("A1", "PLAN-A", null, true), Planner)).Success);
        Assert.True((await service.CreateJobAsync(new JobDraft("A2", "PLAN-A", null, true), Planner)).Success);
    }

    [Fact]
    public async Task UpdateJob_WithStaleVersion_IsAConflict()
    {
        var service = Service();
        await service.CreateJobAsync(new JobDraft("A1", "PLAN-A", null, true), Planner);
        var job = (await _db.Store.ListJobsAsync())[0];

        Assert.True((await service.UpdateJobAsync(job.Id, job.Version, new JobDraft("A1", "PLAN-B", null, true), Planner)).Success);
        var stale = await service.UpdateJobAsync(job.Id, job.Version, new JobDraft("A1", "PLAN-A", null, true), Planner);

        Assert.False(stale.Success);
        Assert.Contains("modificato da planner", stale.Errors[0]);
        Assert.Equal(2, (await _db.Store.GetJobHistoryAsync(job.Id)).Count);
    }

    [Fact]
    public async Task Order_RequiresExistingJob()
    {
        var result = await Service().CreateOrderAsync(new OrderDraft("OP-1", "NOPE", 1, null), Planner);

        Assert.False(result.Success);
        Assert.Contains("non ha un plan associato", result.Errors[0]);
    }

    [Fact]
    public async Task Job_WithOpenOrders_CannotBeDeletedOrRenamed()
    {
        var service = Service();
        await service.CreateJobAsync(new JobDraft("A1", "PLAN-A", null, true), Planner);
        await service.CreateOrderAsync(new OrderDraft("OP-1", "A1", 1, null), Planner);
        var job = (await _db.Store.ListJobsAsync())[0];

        Assert.False((await service.DeleteJobAsync(job.Id, job.Version, Planner)).Success);
        Assert.False((await service.UpdateJobAsync(job.Id, job.Version, new JobDraft("A9", "PLAN-A", null, true), Planner)).Success);
    }

    [Fact]
    public async Task PlanRow_DefaultsToToday_AndCannotBeInThePast()
    {
        var service = Service();
        await service.CreateJobAsync(new JobDraft("A1", "PLAN-A", null, true), Planner);

        Assert.True((await service.CreateOrderAsync(new OrderDraft("", "A1", 1, null), Planner)).Success);
        Assert.Equal(service.Today, (await _db.Store.ListOpenOrdersAsync())[0].WorkDate);

        var past = await service.CreateOrderAsync(new OrderDraft("", "A1", 1, null, service.Today.AddDays(-1)), Planner);
        Assert.False(past.Success);
        Assert.Contains("passato", past.Errors[0]);
    }

    [Fact]
    public async Task ServiceCode_CannotBePlanned()
    {
        var service = Service();
        await service.CreateJobAsync(new JobDraft("HOME", "PLAN-A", null, true, IsService: true), Planner);

        var result = await service.CreateOrderAsync(new OrderDraft("", "HOME", 1, null), Planner);

        Assert.False(result.Success);
        Assert.Contains("codice di servizio", result.Errors[0]);
    }

    [Fact]
    public async Task CopyMissing_RequiresALaterDay()
    {
        var service = Service();

        Assert.False((await service.CopyMissingAsync(service.Today, service.Today, Planner)).Success);
        Assert.True((await service.CopyMissingAsync(service.Today, service.Today.AddDays(1), Planner)).Success);
    }

    [Fact]
    public void CopyTarget_IsTheNextDay_OrTodayForPastDays()
    {
        var service = Service();

        Assert.Equal(service.Today.AddDays(1), service.CopyTargetFor(service.Today));
        Assert.Equal(service.Today, service.CopyTargetFor(service.Today.AddDays(-1)));
        Assert.Equal(service.Today, service.CopyTargetFor(service.Today.AddDays(-5)));
    }

    [Fact]
    public async Task Create_ReturnsTheNewId_AndConflictsAreFlagged()
    {
        var service = Service();
        var job = await service.CreateJobAsync(new JobDraft("A1", "PLAN-A", null, true), Planner);
        var row = await service.CreateOrderAsync(new OrderDraft("", "A1", 2, null), Planner);

        Assert.NotNull(job.Id);
        Assert.Equal("A1", (await _db.Store.GetOrderAsync(row.Id!.Value))?.ProductCode);
        Assert.False(row.Conflict);

        var stale = await service.UpdateJobAsync(job.Id!.Value, 99, new JobDraft("A1", "PLAN-B", null, true), Planner);
        Assert.True(stale.Conflict);
        var invalid = await service.CreateOrderAsync(new OrderDraft("", "A1", 0, null), Planner);
        Assert.False(invalid.Conflict);
    }

    [Fact]
    public async Task Report_ComesFromThePlanOfTheDay()
    {
        var service = Service();
        await service.CreateJobAsync(new JobDraft("A1", "PLAN-A", null, true), Planner);
        await service.CreateOrderAsync(new OrderDraft("", "A1", 3, null), Planner);
        await service.CreateOrderAsync(new OrderDraft("", "A1", 5, null, service.Today.AddDays(1)), Planner);

        var report = await service.GetReportAsync(service.Today);

        Assert.Single(report.Rows);
        Assert.Equal(3, report.Totals.Planned);
        Assert.Equal(3, report.Totals.Missing);
    }
}

public class DailyReportTests
{
    private static readonly DateOnly Day = new(2026, 10, 30);

    private static ProductionOrder Row(string code, int quantity, OrderStatus status, int done = 0) => new()
    {
        ProductCode = code, Quantity = quantity, Status = status, CompletedCount = done, WorkDate = Day,
    };

    [Fact]
    public void Rows_AreClassified_DoneInProgressMissing()
    {
        var report = DailyReport.Build(Day,
        [
            Row("A", 2, OrderStatus.Completed, done: 2),
            Row("B", 4, OrderStatus.Active, done: 1),
            Row("C", 3, OrderStatus.Pending),
            Row("D", 5, OrderStatus.Cancelled, done: 1),
            Row("E", 6, OrderStatus.CarriedOver, done: 2),
        ]);

        Assert.Equal(
            new[] { RowProgress.Done, RowProgress.InProgress, RowProgress.Missing, RowProgress.Cancelled, RowProgress.CarriedOver },
            report.Rows.Select(r => r.Progress));
        var t = report.Totals;
        Assert.Equal((5, 1, 1, 2), (t.Rows, t.RowsDone, t.RowsInProgress, t.RowsMissing));
        Assert.Equal(2 + 4 + 3 + 6, t.Planned);   // cancelled row not expected any more
        Assert.Equal(2 + 1 + 1 + 2, t.Done);      // pieces really made, cancelled row included
        Assert.Equal(0 + 3 + 3 + 4, t.Missing);   // carried-over pieces are still missing on this day
    }

    [Fact]
    public void EmptyDay_HasZeroTotals()
    {
        var report = DailyReport.Build(Day, []);

        Assert.Empty(report.Rows);
        Assert.Equal(0, report.Totals.Planned);
    }
}

public class ScanServiceTests
{
    private readonly FakePlanning _planning = new();
    private readonly EventLog _events = new();
    private readonly ScanService _scanner;

    public ScanServiceTests()
    {
        _planning.AddJob("A100", "PLAN-A");
        _planning.AddJob("B200", "PLAN-B");
        _planning.AddJob("C300", "PLAN-C");
        _planning.AddJob("OFF", "PLAN-A", enabled: false);
        _planning.AddJob("HOME", "PLAN-Home", service: true);
        _planning.AddOrder("OP-1", "B200", 2);
        _planning.AddOrder("OP-2", "A100", 3);
        _planning.AddOrder("OP-3", "A100", 1);
        _planning.AddOrder("OLD", "C300", 1, date: FakePlanning.Today.AddDays(-1));
        _scanner = new ScanService(_planning, _events, TimeProvider.System);
    }

    [Fact]
    public async Task Scan_SelectsFirstOpenRowOfTodayWithThatCode()
    {
        var result = await _scanner.ResolveAsync("a100\r\n", "test");

        Assert.True(result.IsValid);
        Assert.Equal("OP-2", result.Order?.OrderNumber);
        Assert.Equal("PLAN-A", result.Job?.PlanName);
    }

    [Fact]
    public async Task Scan_AnyCodeOfToday_EvenWhileAnotherRowIsInProgress()
    {
        _planning.Orders[0] = _planning.Orders[0] with { Status = OrderStatus.Active }; // OP-1, B200

        var other = await _scanner.ResolveAsync("A100", "test");

        Assert.Equal("OP-2", other.Order?.OrderNumber);
    }

    [Fact]
    public async Task Scan_SkipsCompletedRows()
    {
        _planning.Orders[1] = _planning.Orders[1] with { Status = OrderStatus.Completed, CompletedCount = 3 };

        var result = await _scanner.ResolveAsync("A100", "test");

        Assert.Equal("OP-3", result.Order?.OrderNumber);
    }

    [Fact]
    public async Task Scan_OfCodeNotInTodaysPlan_IsRefused()
    {
        var result = await _scanner.ResolveAsync("C300", "test"); // only in yesterday's plan

        Assert.False(result.IsValid);
        Assert.Contains("non è negli ordini di produzione di oggi", result.Error);
    }

    [Fact]
    public async Task Scan_OfServiceCode_LoadsItWithoutRow()
    {
        var result = await _scanner.ResolveAsync("home", "test");

        Assert.True(result.IsValid);
        Assert.Null(result.Order);
    }

    [Theory]
    [InlineData("ZZZ", "sconosciuto")]
    [InlineData("off", "disattivato")]
    [InlineData("  ", "obbligatorio")]
    public async Task Scan_UnknownDisabledOrEmpty_GivesMessage(string raw, string reason)
    {
        var result = await _scanner.ResolveAsync(raw, "test");

        Assert.False(result.IsValid);
        Assert.Contains(reason, result.Error);
        Assert.Contains(_events.GetRecent(), e => e.Source == "Scanner" && e.Level == EventLevel.Warning);
    }
}
