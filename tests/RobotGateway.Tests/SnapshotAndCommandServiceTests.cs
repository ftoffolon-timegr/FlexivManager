using Microsoft.Extensions.Options;
using RobotGateway.Core;

namespace RobotGateway.Tests;

public class SnapshotStoreTests
{
    [Fact]
    public void Publish_ReplacesCurrent_AndNotifies()
    {
        var store = new SnapshotStore();
        RobotSnapshot? received = null;
        store.SnapshotUpdated += s => received = s;

        var snapshot = new RobotSnapshot { Sequence = 7 };
        store.Publish(snapshot);

        Assert.Same(snapshot, store.Current);
        Assert.Same(snapshot, received);
    }

    [Fact]
    public void Publish_SurvivesFaultySubscriber()
    {
        var store = new SnapshotStore();
        var secondCalled = false;
        store.SnapshotUpdated += _ => throw new InvalidOperationException();
        store.SnapshotUpdated += _ => secondCalled = true;

        store.Publish(new RobotSnapshot { Sequence = 1 });

        Assert.True(secondCalled);
    }
}

/// <summary>Command service without a worker: queue behaviour, confirmation and order rules.</summary>
public class RobotCommandServiceTests
{
    private const string Source = "test";

    private readonly ManualTimeProvider _time = new();
    private readonly SnapshotStore _store = new();
    private readonly EventLog _events = new();
    private readonly FakePlanning _planning = new();
    private readonly RobotCommandService _service;

    public RobotCommandServiceTests()
    {
        _planning.AddJob("A100", "PLAN-Pick");
        _planning.AddJob("OFF", "PLAN-Pick", enabled: false);
        _planning.AddJob("GHOST", "PLAN-NotOnRobot", service: true);
        _planning.AddJob("HOME", "PLAN-Pick", service: true);
        _planning.AddOrder("OP-1", "A100", 2, date: WorkDay.Today(_time));

        var options = Options.Create(new RobotWorkerOptions { ConfirmationTimeout = TimeSpan.FromSeconds(15) });
        _service = new RobotCommandService(options, _store, _planning, new CycleEvents(), _events, _time);
        Publish(RobotServiceState.Operational);
    }

    private void Publish(RobotServiceState state, CycleRecord? lastCycle = null) =>
        _store.Publish(_store.Current with
        {
            Sequence = _store.Current.Sequence + 1,
            State = state,
            AvailablePlans = ["PLAN-Pick"],
            LastCycle = lastCycle ?? _store.Current.LastCycle,
        });

    private PendingCommand Dequeued()
    {
        Assert.True(_service.TryDequeueNormal(out var pending));
        return pending;
    }

    private static CycleRecord Cycle(int? orderId, CycleOutcome outcome) =>
        new("A100", "PLAN-Pick", orderId, "OP-1", Source, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), outcome, null);

    /// <summary>Confirms a start and plays the worker: completes the queued command successfully.</summary>
    private async Task<StartCycleCommand> ConfirmStartAsync(string code, int? orderId)
    {
        var request = await _service.RequestStartAsync(code, orderId, Source);
        Assert.True(request.IsAccepted, request.Rejection);
        var confirm = _service.ConfirmAsync(request.Token!, Source);
        var pending = Dequeued();
        Assert.True(pending.TryStart());
        pending.Complete(CommandResult.Ok());
        Assert.True((await confirm).IsSuccess);
        return Assert.IsType<StartCycleCommand>(pending.Command);
    }

    // ---- Queue -------------------------------------------------------------------------------

    [Fact]
    public async Task TimedOutCommand_IsWithdrawn_AndNeverStarts()
    {
        var result = await _service.SendAsync(new EnableCommand(), Source, TimeSpan.FromMilliseconds(50));

        Assert.Equal(CommandOutcome.TimedOut, result.Outcome);
        Assert.False(Dequeued().TryStart());
    }

    [Fact]
    public async Task CancelledCommand_IsWithdrawn()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await _service.SendAsync(new EnableCommand(), Source, TimeSpan.FromSeconds(5), cts.Token);

        Assert.Equal(CommandOutcome.Cancelled, result.Outcome);
        Assert.False(Dequeued().TryStart());
    }

    [Fact]
    public async Task Stop_IsDequeuedBeforeNormalCommands()
    {
        _ = _service.SendAsync(new EnableCommand(), Source, TimeSpan.FromSeconds(5));
        _ = _service.StopAsync(Source);
        await Task.Yield();

        Assert.True(_service.TryDequeueStop(out var first));
        Assert.IsType<StopCommand>(first.Command);
        Assert.Equal(Source, first.Source);
    }

    [Fact]
    public async Task FlushNormal_CancelsQueuedCommands()
    {
        var a = _service.SendAsync(new EnableCommand(), Source, TimeSpan.FromSeconds(5));
        var b = _service.SendAsync(new ClearFaultCommand(), Source, TimeSpan.FromSeconds(5));

        Assert.Equal(2, _service.FlushNormal("stop"));

        Assert.Equal(CommandOutcome.Cancelled, (await a).Outcome);
        Assert.Equal(CommandOutcome.Cancelled, (await b).Outcome);
    }

    // ---- Confirmation ------------------------------------------------------------------------

    [Fact]
    public async Task MotionCommand_WithoutConfirmation_IsRejected_AndNotQueued()
    {
        var command = new StartCycleCommand(new CycleRequest(_planning.Jobs[0], null, null));

        var result = await _service.SendAsync(command, Source);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.False(_service.TryDequeueNormal(out _));
    }

    [Fact]
    public async Task Request_DoesNotQueueAnything()
    {
        var request = await _service.RequestStartAsync("a100", 1, Source);

        Assert.True(request.IsAccepted);
        Assert.Contains("PLAN-Pick", request.Summary);
        Assert.False(_service.TryDequeueNormal(out _));
    }

    [Fact]
    public async Task Confirm_QueuesTheJobSnapshot_Once()
    {
        var request = await _service.RequestStartAsync("A100", 1, Source);
        // A remote edit after the request does not change what was confirmed.
        _planning.Jobs[0] = _planning.Jobs[0] with { PlanName = "PLAN-Other", Version = 2 };

        var confirmed = _service.ConfirmAsync(request.Token!, Source);
        var pending = Dequeued();
        var command = Assert.IsType<StartCycleCommand>(pending.Command);
        Assert.Equal("PLAN-Pick", command.Request.Job.PlanName);
        Assert.Equal("OP-1", command.Request.OrderNumber);
        pending.TryStart();
        pending.Complete(CommandResult.Ok());
        Assert.True((await confirmed).IsSuccess);

        Assert.Equal(CommandOutcome.Rejected, (await _service.ConfirmAsync(request.Token!, Source)).Outcome);
    }

    [Fact]
    public async Task ExpiredConfirmation_IsRejected()
    {
        var request = await _service.RequestStartAsync("A100", 1, Source);
        _time.Advance(16);

        var result = await _service.ConfirmAsync(request.Token!, Source);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.False(_service.TryDequeueNormal(out _));
    }

    [Fact]
    public async Task CancelledOrUnknownConfirmation_IsRejected()
    {
        var request = await _service.RequestStartAsync("A100", 1, Source);
        Assert.True(_service.CancelConfirmation(request.Token!, Source));

        Assert.Equal(CommandOutcome.Rejected, (await _service.ConfirmAsync(request.Token!, Source)).Outcome);
        Assert.Equal(CommandOutcome.Rejected, (await _service.ConfirmAsync("not-a-token", Source)).Outcome);
    }

    [Fact]
    public async Task Stop_VoidsPendingConfirmations()
    {
        var request = await _service.RequestStartAsync("A100", 1, Source);
        _ = _service.StopAsync(Source);

        Assert.Equal(CommandOutcome.Rejected, (await _service.ConfirmAsync(request.Token!, Source)).Outcome);
    }

    [Theory]
    [InlineData("UNKNOWN", "non ha un plan associato")]
    [InlineData("OFF", "disattivato")]
    [InlineData("GHOST", "non è presente sul robot")]
    public async Task InvalidJob_IsRejected(string code, string reason)
    {
        var request = await _service.RequestStartAsync(code, null, Source);

        Assert.False(request.IsAccepted);
        Assert.Contains(reason, request.Rejection);
    }

    [Theory]
    [InlineData(RobotServiceState.Connected)]
    [InlineData(RobotServiceState.Running)]
    [InlineData(RobotServiceState.Fault)]
    [InlineData(RobotServiceState.EStop)]
    public async Task Request_IsRejected_WhenStateDoesNotAllowIt(RobotServiceState state)
    {
        Publish(state);
        Assert.False((await _service.RequestStartAsync("A100", 1, Source)).IsAccepted);
    }

    [Fact]
    public async Task ClosedOrder_IsRejected()
    {
        _planning.AddOrder("OP-DONE", "A100", 1, OrderStatus.Completed, done: 1);
        Assert.False((await _service.RequestStartAsync("A100", 2, Source)).IsAccepted);
    }

    [Fact]
    public async Task AnyRowOfToday_CanStart_WhileAnotherIsInProgress()
    {
        _planning.Orders[0] = _planning.Orders[0] with { Status = OrderStatus.Active, CompletedCount = 1 };
        var other = _planning.AddOrder("OP-2", "A100", 1, date: WorkDay.Today(_time));

        Assert.True((await _service.RequestStartAsync("", other.Id, Source)).IsAccepted);
        Assert.True((await _service.RequestStartAsync("", 1, Source)).IsAccepted);
    }

    [Fact]
    public async Task RowOfAnotherDay_IsRejected()
    {
        var tomorrow = _planning.AddOrder("OP-T", "A100", 1, date: WorkDay.Today(_time).AddDays(1));

        var request = await _service.RequestStartAsync("", tomorrow.Id, Source);

        Assert.False(request.IsAccepted);
        Assert.Contains("non di oggi", request.Rejection);
    }

    [Fact]
    public async Task CodeWithoutRow_RunsOnlyWhenItIsAServiceCode()
    {
        var planned = await _service.RequestStartAsync("A100", null, Source);
        Assert.False(planned.IsAccepted);
        Assert.Contains("non è un codice di servizio", planned.Rejection);

        Assert.True((await _service.RequestStartAsync("home", null, Source)).IsAccepted);
    }

    [Fact]
    public void Resume_RequiresPaused()
    {
        Assert.False(_service.RequestResume(Source).IsAccepted);
        Publish(RobotServiceState.Paused);
        Assert.True(_service.RequestResume(Source).IsAccepted);
    }

    // ---- Next cycle of an order --------------------------------------------------------------

    [Fact]
    public async Task NextCycle_OfConfirmedOrder_NeedsOnePress()
    {
        Assert.False(_service.CanRepeatWithoutConfirmation(1));
        await ConfirmStartAsync("A100", 1);
        Publish(RobotServiceState.Operational, Cycle(1, CycleOutcome.Completed));

        Assert.True(_service.CanRepeatWithoutConfirmation(1));
        var next = _service.StartNextCycleAsync(1, Source);
        var pending = Dequeued();
        Assert.IsType<StartCycleCommand>(pending.Command);
        pending.TryStart();
        pending.Complete(CommandResult.Ok());
        Assert.True((await next).IsSuccess);
    }

    [Theory]
    [InlineData(CycleOutcome.Failed)]
    [InlineData(CycleOutcome.Stopped)]
    public async Task NextCycle_AfterFaultOrStop_NeedsConfirmationAgain(CycleOutcome outcome)
    {
        await ConfirmStartAsync("A100", 1);
        Publish(RobotServiceState.Operational, Cycle(1, outcome));

        Assert.False(_service.CanRepeatWithoutConfirmation(1));
        Assert.Equal(CommandOutcome.Rejected, (await _service.StartNextCycleAsync(1, Source)).Outcome);
    }

    [Fact]
    public async Task NextCycle_AfterStop_IsDisarmed()
    {
        await ConfirmStartAsync("A100", 1);
        Publish(RobotServiceState.Operational, Cycle(1, CycleOutcome.Completed));
        _ = _service.StopAsync(Source);

        Assert.False(_service.CanRepeatWithoutConfirmation(1));
    }

    [Fact]
    public async Task ServiceJob_IsNeverArmed()
    {
        await ConfirmStartAsync("HOME", null);
        Publish(RobotServiceState.Operational, Cycle(null, CycleOutcome.Completed));

        Assert.False(_service.CanRepeatWithoutConfirmation(1));
    }

    [Fact]
    public async Task Requests_AreLogged()
    {
        await _service.RequestStartAsync("A100", 1, Source);
        await _service.RequestStartAsync("UNKNOWN", null, Source);

        var log = _events.GetRecent();
        Assert.Contains(log, e => e.Source == "Commands" && e.Message.Contains("Conferma richiesta"));
        Assert.Contains(log, e => e.Level == EventLevel.Warning && e.Message.Contains("rifiutat"));
    }
}
