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

public class RobotCommandServiceTests
{
    private static RobotCommandService Create() => new(Options.Create(new RobotWorkerOptions()));

    [Fact]
    public async Task TimedOutCommand_IsWithdrawn_AndNeverStarts()
    {
        using var service = Create();

        var result = await service.SendAsync(new ExecutePlanCommand("P"), TimeSpan.FromMilliseconds(50));

        Assert.Equal(CommandOutcome.TimedOut, result.Outcome);
        Assert.True(service.TryDequeueNormal(out var pending));
        Assert.False(pending.TryStart());
    }

    [Fact]
    public async Task CancelledCommand_IsWithdrawn()
    {
        using var service = Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await service.SendAsync(new EnableCommand(), TimeSpan.FromSeconds(5), cts.Token);

        Assert.Equal(CommandOutcome.Cancelled, result.Outcome);
        Assert.True(service.TryDequeueNormal(out var pending));
        Assert.False(pending.TryStart());
    }

    [Fact]
    public async Task Stop_IsDequeuedBeforeNormalCommands()
    {
        using var service = Create();
        _ = service.SendAsync(new EnableCommand(), TimeSpan.FromSeconds(5));
        _ = service.StopAsync();
        await Task.Yield();

        Assert.True(service.TryDequeueStop(out var first));
        Assert.IsType<StopCommand>(first.Command);
    }

    [Fact]
    public async Task FlushNormal_CancelsQueuedCommands()
    {
        using var service = Create();
        var a = service.SendAsync(new EnableCommand(), TimeSpan.FromSeconds(5));
        var b = service.SendAsync(new ExecutePlanCommand("P"), TimeSpan.FromSeconds(5));

        Assert.Equal(2, service.FlushNormal("stop"));

        Assert.Equal(CommandOutcome.Cancelled, (await a).Outcome);
        Assert.Equal(CommandOutcome.Cancelled, (await b).Outcome);
    }
}
