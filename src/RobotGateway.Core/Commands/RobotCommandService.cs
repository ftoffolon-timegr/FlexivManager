using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace RobotGateway.Core;

/// <summary>
/// Entry point for every command towards the robot. Commands are queued to the worker thread;
/// STOP has its own channel and is always dequeued first.
/// </summary>
public sealed class RobotCommandService : IDisposable
{
    private const int NormalQueueCapacity = 32;

    private readonly Channel<PendingCommand> _stopQueue =
        Channel.CreateUnbounded<PendingCommand>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Channel<PendingCommand> _normalQueue =
        Channel.CreateBounded<PendingCommand>(new BoundedChannelOptions(NormalQueueCapacity) { SingleReader = true });

    private readonly AutoResetEvent _wake = new(false);
    private readonly TimeSpan _defaultTimeout;

    public RobotCommandService(IOptions<RobotWorkerOptions> options)
    {
        _defaultTimeout = options.Value.DefaultCommandTimeout;
    }

    /// <summary>Signalled whenever a command is queued, so the worker reacts without waiting a full cycle.</summary>
    internal WaitHandle WakeHandle => _wake;

    /// <summary>
    /// Queues a command and waits for its result. On timeout or cancellation a command that has not
    /// started yet is withdrawn and will never execute (no late motion).
    /// </summary>
    public async Task<CommandResult> SendAsync(RobotCommand command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var pending = new PendingCommand(command);
        var queue = command is StopCommand ? _stopQueue : _normalQueue;
        if (!queue.Writer.TryWrite(pending))
            return CommandResult.Rejected("Command queue is full.");
        _wake.Set();

        try
        {
            return await pending.Completion.WaitAsync(timeout ?? _defaultTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return pending.TryCancel()
                ? CommandResult.TimedOut($"{command.Name} was not started in time and has been withdrawn.")
                : CommandResult.TimedOut($"{command.Name} started but did not complete in time.");
        }
        catch (OperationCanceledException)
        {
            return pending.TryCancel()
                ? CommandResult.Cancelled($"{command.Name} cancelled before execution.")
                : CommandResult.Cancelled($"{command.Name} cancelled by caller while executing.");
        }
    }

    public Task<CommandResult> StopAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new StopCommand(), cancellationToken: cancellationToken);

    /// <summary>Worker side: the worker drains STOPs before taking any normal command.</summary>
    internal bool TryDequeueStop(out PendingCommand pending) => _stopQueue.Reader.TryRead(out pending!);

    internal bool TryDequeueNormal(out PendingCommand pending) => _normalQueue.Reader.TryRead(out pending!);

    /// <summary>Worker side: cancels every queued normal command (used when a STOP arrives).</summary>
    internal int FlushNormal(string reason)
    {
        var count = 0;
        while (_normalQueue.Reader.TryRead(out var pending))
        {
            if (pending.TryCancel())
            {
                pending.Complete(CommandResult.Cancelled(reason));
                count++;
            }
        }
        return count;
    }

    public void Dispose() => _wake.Dispose();
}

/// <summary>A queued command with its completion and a queued/started/cancelled flag.</summary>
internal sealed class PendingCommand(RobotCommand command)
{
    private const int Queued = 0, Started = 1, Withdrawn = 2;
    private int _state = Queued;
    private readonly TaskCompletionSource<CommandResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RobotCommand Command { get; } = command;
    public Task<CommandResult> Completion => _tcs.Task;

    /// <summary>Worker side: claims the command; false if the caller already withdrew it.</summary>
    public bool TryStart() => Interlocked.CompareExchange(ref _state, Started, Queued) == Queued;

    /// <summary>Caller side: withdraws the command if the worker has not started it.</summary>
    public bool TryCancel() => Interlocked.CompareExchange(ref _state, Withdrawn, Queued) == Queued;

    public void Complete(CommandResult result) => _tcs.TrySetResult(result);
}
