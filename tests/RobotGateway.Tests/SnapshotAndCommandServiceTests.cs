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

/// <summary>Command service without a worker: queue behaviour and the two-step confirmation.</summary>
public class RobotCommandServiceTests
{
    private const string Source = "test";

    private readonly ManualTimeProvider _time = new();
    private readonly SnapshotStore _store = new();
    private readonly EventLog _events = new();
    private readonly RobotCommandService _service;

    public RobotCommandServiceTests()
    {
        var catalog = new ProgramCatalog(
        [
            new WorkProgram { Id = "pick", Name = "Pick", PlanName = "PLAN-Pick", VelocityScale = 50, MaxVelocityScale = 80 },
        ]);
        var options = Options.Create(new RobotWorkerOptions { ConfirmationTimeout = TimeSpan.FromSeconds(15) });
        _service = new RobotCommandService(options, _store, catalog, _events, _time);
        SetState(RobotServiceState.Operational);
    }

    private void SetState(RobotServiceState state) =>
        _store.Publish(_store.Current with { Sequence = _store.Current.Sequence + 1, State = state });

    private PendingCommand Dequeued()
    {
        Assert.True(_service.TryDequeueNormal(out var pending));
        return pending;
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
        var result = await _service.SendAsync(new RunPlanCommand("PLAN-Pick", 50), Source);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.False(_service.TryDequeueNormal(out _));
    }

    [Fact]
    public void Request_DoesNotQueueAnything()
    {
        var request = _service.RequestRunProgram("pick", 50, Source);

        Assert.True(request.IsAccepted);
        Assert.Contains("Pick", request.Summary);
        Assert.False(_service.TryDequeueNormal(out _));
    }

    [Fact]
    public async Task Confirm_QueuesTheCommand_Once()
    {
        var request = _service.RequestRunProgram("pick", 60, Source);

        var confirmed = _service.ConfirmAsync(request.Token!, Source);
        var pending = Dequeued();
        Assert.Equal(new RunPlanCommand("PLAN-Pick", 60), pending.Command);
        Assert.True(pending.TryStart());
        pending.Complete(CommandResult.Ok());
        Assert.True((await confirmed).IsSuccess);

        var second = await _service.ConfirmAsync(request.Token!, Source);
        Assert.Equal(CommandOutcome.Rejected, second.Outcome);
        Assert.False(_service.TryDequeueNormal(out _));
    }

    [Fact]
    public async Task ExpiredConfirmation_IsRejected()
    {
        var request = _service.RequestRunProgram("pick", 50, Source);
        _time.Advance(16);

        var result = await _service.ConfirmAsync(request.Token!, Source);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.False(_service.TryDequeueNormal(out _));
    }

    [Fact]
    public async Task UnknownToken_IsRejected()
    {
        var result = await _service.ConfirmAsync("not-a-token", Source);
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task CancelledConfirmation_CannotBeConfirmed()
    {
        var request = _service.RequestRunProgram("pick", 50, Source);
        Assert.True(_service.CancelConfirmation(request.Token!, Source));

        Assert.Equal(CommandOutcome.Rejected, (await _service.ConfirmAsync(request.Token!, Source)).Outcome);
    }

    [Fact]
    public async Task Stop_VoidsPendingConfirmations()
    {
        var request = _service.RequestRunProgram("pick", 50, Source);
        _ = _service.StopAsync(Source);

        Assert.Equal(CommandOutcome.Rejected, (await _service.ConfirmAsync(request.Token!, Source)).Outcome);
    }

    [Fact]
    public void ProgramNotInCatalog_IsRejected()
    {
        Assert.False(_service.RequestRunProgram("unknown", 50, Source).IsAccepted);
        Assert.False(_service.RequestConfirmation(new RunPlanCommand("PLAN-NotInCatalog", 50), Source).IsAccepted);
    }

    [Fact]
    public void VelocityAboveProgramLimit_IsRejected()
    {
        var request = _service.RequestRunProgram("pick", 81, Source);

        Assert.False(request.IsAccepted);
        Assert.Contains("80", request.Rejection);
    }

    [Theory]
    [InlineData(RobotServiceState.Connected)]
    [InlineData(RobotServiceState.Running)]
    [InlineData(RobotServiceState.Fault)]
    [InlineData(RobotServiceState.EStop)]
    public void Request_IsRejected_WhenStateDoesNotAllowIt(RobotServiceState state)
    {
        SetState(state);
        Assert.False(_service.RequestRunProgram("pick", 50, Source).IsAccepted);
    }

    [Fact]
    public void Resume_RequiresPaused()
    {
        Assert.False(_service.RequestResume(Source).IsAccepted);
        SetState(RobotServiceState.Paused);
        Assert.True(_service.RequestResume(Source).IsAccepted);
    }

    [Fact]
    public void NonMotionCommand_CannotBeConfirmed() =>
        Assert.False(_service.RequestConfirmation(new EnableCommand(), Source).IsAccepted);

    [Fact]
    public void Requests_AreLogged()
    {
        _service.RequestRunProgram("pick", 50, Source);
        _service.RequestRunProgram("pick", 99, Source);

        var log = _events.GetRecent();
        Assert.Contains(log, e => e.Source == "Commands" && e.Message.Contains("Confirmation requested"));
        Assert.Contains(log, e => e.Level == EventLevel.Warning && e.Message.Contains("refused"));
    }
}
