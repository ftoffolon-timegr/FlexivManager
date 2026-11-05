using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RobotGateway.Core;
using RobotGateway.Mock;

namespace RobotGateway.Tests;

/// <summary>
/// Integration tests: real worker thread + mock backend + cycle recorder + SQLite store,
/// real time with short plan durations.
/// </summary>
public sealed class RobotWorkerTests : IAsyncLifetime
{
    private const string Source = "test";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly TempPlanningDb _db = new();
    private readonly MockBackend _robot;
    private readonly SnapshotStore _store = new();
    private readonly EventLog _events = new();
    private readonly CycleEvents _cycleEvents = new();
    private readonly RobotCommandService _commands;
    private readonly RobotWorker _worker;
    private readonly CycleRecorder _recorder;

    public RobotWorkerTests()
    {
        var mockOptions = new MockBackendOptions
        {
            EnableDelay = TimeSpan.FromMilliseconds(100),
            Plans =
            [
                new("Short", TimeSpan.FromMilliseconds(750), ["A", "B"]),
                new("Long", TimeSpan.FromSeconds(30), ["A", "B"]),
                new("Faulty", TimeSpan.FromSeconds(30), ["A"], FaultAfter: TimeSpan.FromMilliseconds(200)),
            ],
        };
        var workerOptions = Options.Create(new RobotWorkerOptions
        {
            PollRateHz = 100,
            ReconnectInterval = TimeSpan.FromMilliseconds(100),
            DefaultCommandTimeout = TimeSpan.FromSeconds(2),
        });

        _robot = new MockBackend(mockOptions, TimeProvider.System);
        _commands = new RobotCommandService(workerOptions, _store, _db.Store, _cycleEvents, _events, TimeProvider.System);
        _worker = new RobotWorker(_robot, _commands, _store, _events, _cycleEvents, workerOptions, TimeProvider.System, NullLogger<RobotWorker>.Instance);
        _recorder = new CycleRecorder(_cycleEvents, _db.Store, _events, TimeProvider.System, NullLogger<CycleRecorder>.Instance);
    }

    public async Task InitializeAsync()
    {
        await _db.Store.CreateJobAsync(new JobDraft("SHORT", "Short", null, true), "test");
        await _db.Store.CreateJobAsync(new JobDraft("LONG", "Long", null, true), "test");
        await _db.Store.CreateJobAsync(new JobDraft("FAULTY", "Faulty", null, true), "test");
        // Today's work plan: the operator can run only these codes.
        foreach (var code in new[] { "SHORT", "LONG", "FAULTY" })
            await _db.Store.CreateOrderAsync(new OrderDraft("", code, 100, null), "test");
        await _recorder.StartAsync(CancellationToken.None);
        await _worker.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _worker.StopAsync(CancellationToken.None);
        await _recorder.StopAsync(CancellationToken.None);
        _worker.Dispose();
        _recorder.Dispose();
        _commands.Dispose();
        _db.Dispose();
    }

    private async Task<RobotSnapshot> WaitFor(Func<RobotSnapshot, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = _store.Current;
            if (condition(snapshot)) return snapshot;
            await Task.Delay(5);
        }
        throw new TimeoutException($"{what} not reached; state {_store.Current.State}.");
    }

    private Task<RobotSnapshot> WaitForStateAsync(RobotServiceState state) => WaitFor(s => s.State == state, $"State {state}");

    private async Task Send(RobotCommand command)
    {
        var result = await _commands.SendAsync(command, Source);
        Assert.True(result.IsSuccess, $"{command.Name}: {result.Outcome} {result.Message}");
    }

    private async Task EnableAsync()
    {
        await WaitForStateAsync(RobotServiceState.Connected);
        await Send(new EnableCommand());
        await WaitForStateAsync(RobotServiceState.Operational);
    }

    /// <summary>Two-step start: request + confirm. Without an explicit row, the open row of today with the code.</summary>
    private async Task<CommandResult> StartAsync(string code, int? orderId = null)
    {
        orderId ??= (await _db.Store.ListPlanAsync(WorkDay.Today(TimeProvider.System)))
            .First(o => o.IsOpen && o.ProductCode == code).Id;
        var request = await _commands.RequestStartAsync(code, orderId, Source);
        Assert.True(request.IsAccepted, request.Rejection);
        return await _commands.ConfirmAsync(request.Token!, Source);
    }

    private async Task Started(string code, int? orderId = null)
    {
        var result = await StartAsync(code, orderId);
        Assert.True(result.IsSuccess, $"Start {code}: {result.Outcome} {result.Message}");
        await WaitFor(s => s.ActiveCycle is not null && s.State == RobotServiceState.Running, "cycle running");
    }

    private Task<RobotSnapshot> CycleEnded() => WaitFor(s => s.ActiveCycle is null && s.LastCycle is not null, "cycle end");

    // ---- Lifecycle ---------------------------------------------------------------------------

    [Fact]
    public async Task Startup_ConnectsButNeverEnablesOrMoves()
    {
        await WaitForStateAsync(RobotServiceState.Connected);
        await Task.Delay(300);

        var snapshot = _store.Current;
        Assert.Equal(RobotServiceState.Connected, snapshot.State);
        Assert.False(snapshot.Status.Operational);
        Assert.False(snapshot.Status.Busy);
        Assert.NotNull(snapshot.States);
        Assert.Equal(3, snapshot.AvailablePlans.Length);
    }

    [Fact]
    public async Task Snapshot_IsPublishedContinuously()
    {
        await WaitForStateAsync(RobotServiceState.Connected);
        var first = _store.Current.Sequence;
        await Task.Delay(200);
        Assert.True(_store.Current.Sequence > first + 5);
    }

    [Fact]
    public async Task Start_IsRejected_WhenNotOperational()
    {
        await WaitForStateAsync(RobotServiceState.Connected);

        var row = (await _db.Store.ListPlanAsync(WorkDay.Today(TimeProvider.System))).First(o => o.ProductCode == "SHORT");
        var request = await _commands.RequestStartAsync("", row.Id, Source);
        Assert.False(request.IsAccepted);
        Assert.Contains("richiede", request.Rejection);
        Assert.False(_robot.IsBusy);
    }

    // ---- Cycles ------------------------------------------------------------------------------

    [Fact]
    public async Task Cycle_RunsToCompletion_AndCountsForItsRow()
    {
        await EnableAsync();
        await Started("SHORT");
        Assert.Equal("Short", _store.Current.ActiveCycle!.PlanName);

        var done = await CycleEnded();
        Assert.Equal(CycleOutcome.Completed, done.LastCycle!.Outcome);
        Assert.NotNull(done.LastCycle.OrderId);
        Assert.Equal(1, done.CompletedCycles);
        Assert.True(await _cycleEvents.WaitIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.Single(await _db.Store.ListCyclesAsync(10));
    }

    [Fact]
    public async Task Order_FirstCycleConfirmed_NextOnePress_CompletesAtQuantity()
    {
        var order = await _db.Store.CreateOrderAsync(new OrderDraft("OP-1", "SHORT", 2, null), "test");
        await EnableAsync();

        await Started("", order.Id);
        await CycleEnded();
        await WaitForStateAsync(RobotServiceState.Operational);
        Assert.True(await _cycleEvents.WaitIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal((1, OrderStatus.Active), await OrderProgress(order.Id));

        Assert.True(_commands.CanRepeatWithoutConfirmation(order.Id));
        var next = await _commands.StartNextCycleAsync(order.Id, Source);
        Assert.True(next.IsSuccess, next.Message);
        await WaitFor(s => s.ActiveCycle is null && s.CompletedCycles == 2, "second cycle end");
        Assert.True(await _cycleEvents.WaitIdleAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal((2, OrderStatus.Completed), await OrderProgress(order.Id));
        // Completed: the next start of this order is refused.
        Assert.False((await _commands.RequestStartAsync("", order.Id, Source)).IsAccepted);
    }

    [Fact]
    public async Task Fault_StopsTheCycle_NotCounted_RestartNeedsConfirmation()
    {
        var order = await _db.Store.CreateOrderAsync(new OrderDraft("OP-F", "FAULTY", 3, null), "test");
        await EnableAsync();

        await Started("", order.Id);
        var fault = await WaitForStateAsync(RobotServiceState.Fault);
        Assert.Null(fault.ActiveCycle);
        Assert.Equal(CycleOutcome.Failed, fault.LastCycle!.Outcome);
        Assert.True(await _cycleEvents.WaitIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal((0, OrderStatus.Active), await OrderProgress(order.Id));
        Assert.False(_commands.CanRepeatWithoutConfirmation(order.Id));

        await Send(new ClearFaultCommand());
        await WaitForStateAsync(RobotServiceState.Connected);
        await Send(new EnableCommand());
        await WaitForStateAsync(RobotServiceState.Operational);
    }

    [Fact]
    public async Task Stop_EndsTheCycleAsStopped()
    {
        await EnableAsync();
        await Started("LONG");

        await Send(new StopCommand());

        var snapshot = await WaitForStateAsync(RobotServiceState.Operational);
        Assert.Equal(CycleOutcome.Stopped, snapshot.LastCycle!.Outcome);
        Assert.Equal(RobotMode.Idle, snapshot.Status.Mode);
        Assert.Equal(0, snapshot.CompletedCycles);
        Assert.False(_robot.IsBusy);
    }

    [Fact]
    public async Task PauseResumeStopPlan()
    {
        await EnableAsync();
        await Started("LONG");

        await Send(new PausePlanCommand());
        await WaitForStateAsync(RobotServiceState.Paused);

        var resume = _commands.RequestResume(Source);
        Assert.True((await _commands.ConfirmAsync(resume.Token!, Source)).IsSuccess);
        await WaitForStateAsync(RobotServiceState.Running);

        await Send(new StopPlanCommand());
        var snapshot = await WaitForStateAsync(RobotServiceState.Operational);
        Assert.Equal(CycleOutcome.Stopped, snapshot.LastCycle!.Outcome);
    }

    [Fact]
    public async Task EditDuringCycle_AppliesToNextCycle()
    {
        await EnableAsync();
        await Started("LONG");
        var job = (await _db.Store.FindJobByCodeAsync("LONG"))!;
        await _db.Store.UpdateJobAsync(job.Id, job.Version, new JobDraft("LONG", "Short", null, true), "planner");

        Assert.Equal("Long", _store.Current.ActiveCycle?.PlanName);
        await Send(new StopPlanCommand());
        await WaitForStateAsync(RobotServiceState.Operational);

        await Started("LONG");
        Assert.Equal("Short", _store.Current.ActiveCycle!.PlanName);
    }

    [Fact]
    public async Task Cycle_ResetsTheRobotVelocityOverride_To100()
    {
        await EnableAsync();
        _robot.SetVelocityScale(30); // left over from Elements or the pendant
        await Started("LONG");

        Assert.Equal(100, _robot.GetPlanInfo().VelocityScale);
    }

    [Fact]
    public async Task Stop_JumpsTheQueue_AndCancelsQueuedCommands()
    {
        await EnableAsync();
        await Started("LONG");

        var queued = Enumerable.Range(0, 10)
            .Select(_ => _commands.SendAsync(new EnableCommand(), Source))
            .ToArray();
        var stop = await _commands.StopAsync(Source);

        Assert.True(stop.IsSuccess);
        await WaitForStateAsync(RobotServiceState.Operational);
        var results = await Task.WhenAll(queued);
        Assert.Contains(results, r => r.Outcome == CommandOutcome.Cancelled);
    }

    // ---- Robot conditions --------------------------------------------------------------------

    [Fact]
    public async Task EStop_FailsTheCycle_AndReleaseRequiresEnable()
    {
        await EnableAsync();
        await Started("LONG");

        _robot.SetEStop(true);
        await WaitForStateAsync(RobotServiceState.EStop);
        await WaitFor(s => s.LastCycle?.Outcome == CycleOutcome.Failed, "cycle failed");
        Assert.Equal(CommandOutcome.Rejected, (await _commands.EnableAsync(Source)).Outcome);

        _robot.SetEStop(false);
        await WaitForStateAsync(RobotServiceState.Connected);
    }

    [Fact]
    public async Task LinkLoss_IsDetected_AndReconnects()
    {
        await EnableAsync();

        _robot.SimulateLinkLoss(true);
        var lost = await WaitForStateAsync(RobotServiceState.Disconnected);
        Assert.NotNull(lost.LastError);
        Assert.Null(lost.States);

        _robot.SimulateLinkLoss(false);
        var back = await WaitForStateAsync(RobotServiceState.Connected);
        Assert.Null(back.LastError);
        Assert.False(back.Status.Operational); // reconnecting never re-enables the robot
    }

    [Fact]
    public async Task ManualDisconnect_SuppressesAutoReconnect()
    {
        await WaitForStateAsync(RobotServiceState.Connected);

        await Send(new DisconnectCommand());
        await WaitForStateAsync(RobotServiceState.Disconnected);
        await Task.Delay(300);
        Assert.Equal(RobotServiceState.Disconnected, _store.Current.State);

        await Send(new ConnectCommand());
        await WaitForStateAsync(RobotServiceState.Connected);
    }

    [Fact]
    public async Task Shutdown_StopsRunningCycle()
    {
        await EnableAsync();
        await Started("LONG");

        await _worker.StopAsync(CancellationToken.None);

        Assert.False(_robot.IsConnected);
        Assert.Equal(RobotServiceState.Disconnected, _store.Current.State);
        Assert.Equal(CycleOutcome.Stopped, _store.Current.LastCycle?.Outcome);
    }

    private async Task<(int, OrderStatus)> OrderProgress(int id)
    {
        var order = (await _db.Store.GetOrderAsync(id))!;
        return (order.CompletedCount, order.Status);
    }
}
