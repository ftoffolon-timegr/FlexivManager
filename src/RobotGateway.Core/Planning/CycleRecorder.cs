using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RobotGateway.Core;

public abstract record CycleEvent;
public sealed record CycleStarted(ActiveCycle Cycle) : CycleEvent;
public sealed record CycleFinished(CycleRecord Record) : CycleEvent;

/// <summary>
/// Hand-off from the worker thread (never blocks, never touches the database) to <see cref="CycleRecorder"/>.
/// </summary>
public sealed class CycleEvents
{
    private readonly Channel<CycleEvent> _channel = Channel.CreateUnbounded<CycleEvent>(new UnboundedChannelOptions { SingleReader = true });
    private int _pending;

    /// <summary>Worker side.</summary>
    public void Publish(CycleEvent e)
    {
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(e))
            Interlocked.Decrement(ref _pending);
    }

    internal ChannelReader<CycleEvent> Reader => _channel.Reader;

    internal void MarkProcessed() => Interlocked.Decrement(ref _pending);

    public bool IsIdle => Volatile.Read(ref _pending) == 0;

    /// <summary>Waits until every published event has been stored (e.g. before starting the next cycle of an order).</summary>
    public async Task<bool> WaitIdleAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!IsIdle)
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(10, ct);
        }
        return true;
    }
}

/// <summary>
/// Operator host: persists cycle events. Marks an order Active at its first cycle, stores every cycle in the
/// history, counts Completed cycles and completes the order when the quantity is reached.
/// </summary>
public sealed class CycleRecorder(
    CycleEvents cycleEvents,
    IPlanningStore store,
    EventLog events,
    TimeProvider time,
    ILogger<CycleRecorder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var e in cycleEvents.Reader.ReadAllAsync(stoppingToken))
            {
                try { await HandleAsync(e, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Failed to record {Event}", e);
                    Log(EventLevel.Error, $"Cycle history not saved: {ex.Message}");
                }
                finally { cycleEvents.MarkProcessed(); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(CycleEvent e, CancellationToken ct)
    {
        switch (e)
        {
            case CycleStarted { Cycle.OrderId: { } orderId } started:
                await store.MarkOrderActiveAsync(orderId, started.Cycle.Source, ct);
                break;

            case CycleFinished finished:
                var order = await store.RecordCycleAsync(finished.Record, ct);
                if (order is not null && finished.Record.Outcome == CycleOutcome.Completed)
                {
                    Log(EventLevel.Info, order.Status == OrderStatus.Completed
                        ? $"Order {order.OrderNumber} completed ({order.CompletedCount}/{order.Quantity})."
                        : $"Order {order.OrderNumber}: {order.CompletedCount}/{order.Quantity} done.");
                }
                break;
        }
    }

    private void Log(EventLevel level, string message) =>
        events.Add(new RobotEvent(time.GetUtcNow(), level, "Orders", message));
}
