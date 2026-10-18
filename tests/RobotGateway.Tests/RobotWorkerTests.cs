using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RobotGateway.Core;
using RobotGateway.Mock;

namespace RobotGateway.Tests;

/// <summary>Integration tests: real worker thread + mock backend, real time with short durations.</summary>
public sealed class RobotWorkerTests : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly MockBackend _robot;
    private readonly SnapshotStore _store = new();
    private readonly EventLog _events = new();
    private readonly RobotCommandService _commands;
    private readonly RobotWorker _worker;

    public RobotWorkerTests()
    {
        var mockOptions = new MockBackendOptions
        {
            EnableDelay = TimeSpan.FromMilliseconds(100),
            Plans =
            [
                new("Short", TimeSpan.FromMilliseconds(300), ["A", "B"]),
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

        var catalog = new ProgramCatalog(
        [
            new WorkProgram { Id = "short", Name = "Short", PlanName = "Short" },
            new WorkProgram { Id = "long", Name = "Long", PlanName = "Long" },
            new WorkProgram { Id = "faulty", Name = "Faulty", PlanName = "Faulty" },
            new WorkProgram { Id = "ghost", Name = "Not on robot", PlanName = "DoesNotExist" },
        ]);

        _robot = new MockBackend(mockOptions, TimeProvider.System);
        _commands = new RobotCommandService(workerOptions, _store, catalog, _events, TimeProvider.System);
        _worker = new RobotWorker(_robot, _commands, _store, _events, workerOptions, TimeProvider.System, NullLogger<RobotWorker>.Instance);
    }

    public Task InitializeAsync() => _worker.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        await _worker.StopAsync(CancellationToken.None);
        _worker.Dispose();
        _commands.Dispose();
    }

    private async Task<RobotSnapshot> WaitForStateAsync(RobotServiceState state)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = _store.Current;
            if (snapshot.State == state) return snapshot;
            await Task.Delay(5);
        }
        throw new TimeoutException($"State {state} not reached; current {_store.Current.State}.");
    }

    private const string Source = "test";

    private async Task<CommandResult> Send(RobotCommand command)
    {
        var result = await _commands.SendAsync(command, Source);
        Assert.True(result.IsSuccess, $"{command.Name}: {result.Outcome} {result.Message}");
        return result;
    }

    /// <summary>Two-step motion: request + confirm.</summary>
    private async Task<CommandResult> Confirmed(RobotCommand command)
    {
        var request = _commands.RequestConfirmation(command, Source);
        Assert.True(request.IsAccepted, request.Rejection);
        return await _commands.ConfirmAsync(request.Token!, Source);
    }

    private async Task RunPlan(string plan, int velocity = 100)
    {
        var result = await Confirmed(new RunPlanCommand(plan, velocity));
        Assert.True(result.IsSuccess, $"RunPlan {plan}: {result.Outcome} {result.Message}");
    }

    private async Task EnableAsync()
    {
        await WaitForStateAsync(RobotServiceState.Connected);
        await Send(new EnableCommand());
        await WaitForStateAsync(RobotServiceState.Operational);
    }

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
    public async Task RunPlan_IsRejected_WhenNotOperational()
    {
        await WaitForStateAsync(RobotServiceState.Connected);

        var request = _commands.RequestConfirmation(new RunPlanCommand("Short", 100), Source);

        Assert.False(request.IsAccepted);
        Assert.False(_robot.IsBusy);
    }

    [Fact]
    public async Task PlanMissingOnRobot_IsRejected()
    {
        await EnableAsync();

        var result = await Confirmed(new RunPlanCommand("DoesNotExist", 100));

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task PlanCycle_RunsToCompletion_AndIsCounted()
    {
        await EnableAsync();

        await RunPlan("Short");
        var running = await WaitForStateAsync(RobotServiceState.Running);
        Assert.Equal("Short", running.CurrentPlan);

        var done = await WaitForStateAsync(RobotServiceState.Operational);
        Assert.Equal(1, done.CompletedCycles);
        Assert.NotNull(done.LastCycleTime);
    }

    [Fact]
    public async Task Stop_StopsRunningPlan()
    {
        await EnableAsync();
        await RunPlan("Long");
        await WaitForStateAsync(RobotServiceState.Running);

        await Send(new StopCommand());

        var snapshot = await WaitForStateAsync(RobotServiceState.Operational);
        Assert.Equal(RobotMode.Idle, snapshot.Status.Mode);
        Assert.Equal(0, snapshot.CompletedCycles);
        Assert.False(_robot.IsBusy);
    }

    [Fact]
    public async Task PauseResumeStopPlan()
    {
        await EnableAsync();
        await RunPlan("Long");
        await WaitForStateAsync(RobotServiceState.Running);

        await Send(new PausePlanCommand());
        await WaitForStateAsync(RobotServiceState.Paused);

        Assert.True((await Confirmed(new ResumePlanCommand())).IsSuccess);
        await WaitForStateAsync(RobotServiceState.Running);

        await Send(new StopPlanCommand());
        await WaitForStateAsync(RobotServiceState.Operational);
    }

    [Fact]
    public async Task Fault_ThenClearFault_ThenEnable()
    {
        await EnableAsync();
        await RunPlan("Faulty");

        var fault = await WaitForStateAsync(RobotServiceState.Fault);
        Assert.Null(fault.CurrentPlan);

        await Send(new ClearFaultCommand());
        await WaitForStateAsync(RobotServiceState.Connected);
        await Send(new EnableCommand());
        await WaitForStateAsync(RobotServiceState.Operational);
    }

    [Fact]
    public async Task EStop_IsReported_AndReleaseRequiresEnable()
    {
        await EnableAsync();
        await RunPlan("Long");
        await WaitForStateAsync(RobotServiceState.Running);

        _robot.SetEStop(true);
        await WaitForStateAsync(RobotServiceState.EStop);
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
    public async Task Shutdown_StopsRunningPlan()
    {
        await EnableAsync();
        await RunPlan("Long");
        await WaitForStateAsync(RobotServiceState.Running);

        await _worker.StopAsync(CancellationToken.None);

        Assert.False(_robot.IsConnected);
        Assert.Equal(RobotServiceState.Disconnected, _store.Current.State);
    }

    [Fact]
    public async Task RunPlan_AppliesVelocityScale()
    {
        await EnableAsync();
        await RunPlan("Long", 40);

        var running = await WaitForStateAsync(RobotServiceState.Running);
        await Task.Delay(50);
        Assert.Equal(40, _store.Current.Plan?.VelocityScale);
        Assert.NotNull(running.CurrentPlanStartedAt);
    }

    [Fact]
    public async Task Stop_JumpsTheQueue_AndCancelsQueuedCommands()
    {
        await EnableAsync();
        await RunPlan("Long");
        await WaitForStateAsync(RobotServiceState.Running);

        // Normal commands queued behind each other; STOP must not wait for them.
        var queued = Enumerable.Range(0, 10)
            .Select(_ => _commands.SendAsync(new SetVelocityScaleCommand(50), Source))
            .ToArray();
        var stop = await _commands.StopAsync(Source);

        Assert.True(stop.IsSuccess);
        await WaitForStateAsync(RobotServiceState.Operational);
        var results = await Task.WhenAll(queued);
        Assert.Contains(results, r => r.Outcome == CommandOutcome.Cancelled);
    }
}
