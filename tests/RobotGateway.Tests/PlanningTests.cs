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
        Assert.Empty(PlanningValidator.Validate(new JobDraft("A1", "PLAN-A", 50, null, true), 80, RobotPlans));

    [Fact]
    public void Job_PlanMustBeOnRobot_AndVelocityWithinGlobalLimit()
    {
        var errors = PlanningValidator.Validate(new JobDraft("A1", "PLAN-X", 90, null, true), 80, RobotPlans);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Job_PlanNotCheckedWhenRobotListUnknown() =>
        Assert.Empty(PlanningValidator.Validate(new JobDraft("A1", "PLAN-X", 50, null, true), 80, []));

    [Fact]
    public void Order_RequiresNumberCodeAndPositiveQuantity()
    {
        Assert.Equal(3, PlanningValidator.Validate(new OrderDraft("", "", 0, null)).Count);
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

    private PlanningService Service(bool requireLogin = true) => new(
        _db.Store, _snapshots,
        Options.Create(new RobotWorkerOptions { MaxVelocityScale = 80 }),
        Options.Create(new PlanningOptions { RequireAuthenticatedUser = requireLogin }),
        _events, TimeProvider.System);

    [Fact]
    public async Task Edits_RequireAuthenticatedUser()
    {
        var result = await Service().CreateJobAsync(new JobDraft("A1", "PLAN-A", 50, null, true), Anonymous);

        Assert.False(result.Success);
        Assert.Empty(await _db.Store.ListJobsAsync());
    }

    [Fact]
    public async Task Edits_AllowedWithoutLogin_WhenDisabled()
    {
        var result = await Service(requireLogin: false).CreateJobAsync(new JobDraft("a1", "PLAN-A", 50, null, true), Anonymous);

        Assert.True(result.Success);
        var job = Assert.Single(await _db.Store.ListJobsAsync());
        Assert.Equal("A1", job.ProductCode);
        Assert.Equal("anonymous", job.UpdatedBy);
    }

    [Fact]
    public async Task CreateJob_ValidatesAndLogsWho()
    {
        var service = Service();
        Assert.False((await service.CreateJobAsync(new JobDraft("A1", "PLAN-X", 50, null, true), Planner)).Success);
        Assert.True((await service.CreateJobAsync(new JobDraft("A1", "PLAN-A", 50, null, true), Planner)).Success);
        Assert.False((await service.CreateJobAsync(new JobDraft("a1", "PLAN-B", 50, null, true), Planner)).Success); // duplicate code

        Assert.Contains(_events.GetRecent(), e => e.Message.Contains("created by planner"));
    }

    [Fact]
    public async Task SeveralCodes_MayUseTheSamePlan()
    {
        var service = Service();
        Assert.True((await service.CreateJobAsync(new JobDraft("A1", "PLAN-A", 50, null, true), Planner)).Success);
        Assert.True((await service.CreateJobAsync(new JobDraft("A2", "PLAN-A", 30, null, true), Planner)).Success);
    }

    [Fact]
    public async Task UpdateJob_WithStaleVersion_IsAConflict()
    {
        var service = Service();
        await service.CreateJobAsync(new JobDraft("A1", "PLAN-A", 50, null, true), Planner);
        var job = (await _db.Store.ListJobsAsync())[0];

        Assert.True((await service.UpdateJobAsync(job.Id, job.Version, new JobDraft("A1", "PLAN-B", 40, null, true), Planner)).Success);
        var stale = await service.UpdateJobAsync(job.Id, job.Version, new JobDraft("A1", "PLAN-A", 60, null, true), Planner);

        Assert.False(stale.Success);
        Assert.Contains("changed by planner", stale.Errors[0]);
        Assert.Equal(2, (await _db.Store.GetJobHistoryAsync(job.Id)).Count);
    }

    [Fact]
    public async Task Order_RequiresExistingJob()
    {
        var result = await Service().CreateOrderAsync(new OrderDraft("OP-1", "NOPE", 1, null), Planner);

        Assert.False(result.Success);
        Assert.Contains("No job", result.Errors[0]);
    }

    [Fact]
    public async Task Job_WithOpenOrders_CannotBeDeletedOrRenamed()
    {
        var service = Service();
        await service.CreateJobAsync(new JobDraft("A1", "PLAN-A", 50, null, true), Planner);
        await service.CreateOrderAsync(new OrderDraft("OP-1", "A1", 1, null), Planner);
        var job = (await _db.Store.ListJobsAsync())[0];

        Assert.False((await service.DeleteJobAsync(job.Id, job.Version, Planner)).Success);
        Assert.False((await service.UpdateJobAsync(job.Id, job.Version, new JobDraft("A9", "PLAN-A", 50, null, true), Planner)).Success);
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
        _planning.AddJob("OFF", "PLAN-A", enabled: false);
        _planning.AddOrder("OP-1", "B200", 2);
        _planning.AddOrder("OP-2", "A100", 3);
        _planning.AddOrder("OP-3", "A100", 1);
        _scanner = new ScanService(_planning, _events, TimeProvider.System);
    }

    [Fact]
    public async Task Scan_SelectsFirstOpenOrderWithThatCode()
    {
        var result = await _scanner.ResolveAsync("a100\r\n", "test");

        Assert.True(result.IsValid);
        Assert.Equal("OP-2", result.Order?.OrderNumber);
        Assert.Equal("PLAN-A", result.Job?.PlanName);
    }

    [Fact]
    public async Task Scan_OfAnotherOrder_IsRefused_WhileAnOrderIsInProgress()
    {
        _planning.Orders[0] = _planning.Orders[0] with { Status = OrderStatus.Active }; // OP-1, B200

        var other = await _scanner.ResolveAsync("A100", "test");
        Assert.False(other.IsValid);
        Assert.Contains("OP-1 is in progress", other.Error);

        var same = await _scanner.ResolveAsync("B200", "test");
        Assert.Equal("OP-1", same.Order?.OrderNumber);
    }

    [Fact]
    public async Task Scan_OfBareJob_IsRefused_WhileAnOrderIsInProgress()
    {
        _planning.AddJob("C300", "PLAN-C");                                             // no order for it
        _planning.Orders[0] = _planning.Orders[0] with { Status = OrderStatus.Active };

        var result = await _scanner.ResolveAsync("C300", "test");

        Assert.False(result.IsValid);
        Assert.Contains("OP-1 is in progress", result.Error);
    }

    [Fact]
    public async Task Scan_IsFreeAgain_AfterActiveOrderCompletes()
    {
        _planning.Orders[0] = _planning.Orders[0] with { Status = OrderStatus.Completed, CompletedCount = 2 };

        var result = await _scanner.ResolveAsync("A100", "test");

        Assert.Equal("OP-2", result.Order?.OrderNumber);
    }

    [Fact]
    public async Task Scan_WithoutOpenOrder_LoadsOneOffJob()
    {
        _planning.Orders.Clear();

        var result = await _scanner.ResolveAsync("A100", "test");

        Assert.True(result.IsValid);
        Assert.Null(result.Order);
    }

    [Theory]
    [InlineData("ZZZ", "Unknown")]
    [InlineData("off", "disabled")]
    [InlineData("  ", "required")]
    public async Task Scan_UnknownDisabledOrEmpty_GivesMessage(string raw, string reason)
    {
        var result = await _scanner.ResolveAsync(raw, "test");

        Assert.False(result.IsValid);
        Assert.Contains(reason, result.Error);
        Assert.Contains(_events.GetRecent(), e => e.Source == "Scanner" && e.Level == EventLevel.Warning);
    }
}
