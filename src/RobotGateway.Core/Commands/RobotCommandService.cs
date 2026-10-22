using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace RobotGateway.Core;

/// <summary>Outcome of the first step of a motion command.</summary>
/// <param name="Token">Single-use token to pass to <see cref="RobotCommandService.ConfirmAsync"/>; null when rejected.</param>
/// <param name="Rejection">Why the request was refused; null when accepted.</param>
/// <param name="Summary">What will happen, for the confirmation dialog.</param>
/// <param name="ExpiresAt">The token is invalid after this instant.</param>
public sealed record ConfirmationRequest(string? Token, string? Rejection, string Summary, DateTimeOffset ExpiresAt)
{
    public bool IsAccepted => Token is not null;
}

/// <summary>
/// The only entry point for commands towards the robot (registered on the operator host only).
/// <list type="bullet">
/// <item>Non-motion commands: <see cref="SendAsync"/> or the helpers (EnableAsync, StopAsync, ...).</item>
/// <item>Motion commands (<see cref="RobotCommand.IsMotion"/>): two steps, <see cref="RequestConfirmation"/>
/// then <see cref="ConfirmAsync"/> with the single-use token before it expires.</item>
/// <item>STOP has its own queue, is always dequeued first, flushes queued commands and voids pending confirmations.</item>
/// </list>
/// Every command is re-checked by <see cref="CommandPolicy"/> on the worker thread right before execution.
/// </summary>
public sealed class RobotCommandService : IDisposable
{
    private const int NormalQueueCapacity = 32;

    private readonly Channel<PendingCommand> _stopQueue =
        Channel.CreateUnbounded<PendingCommand>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Channel<PendingCommand> _normalQueue =
        Channel.CreateBounded<PendingCommand>(new BoundedChannelOptions(NormalQueueCapacity) { SingleReader = true });

    private readonly ConcurrentDictionary<string, PendingConfirmation> _confirmations = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly RobotWorkerOptions _options;
    private readonly SnapshotStore _snapshots;
    private readonly IPlanningReader _planning;
    private readonly CycleEvents _cycleEvents;
    private readonly EventLog _events;
    private readonly TimeProvider _time;

    // Order whose first cycle was confirmed: its next cycles start with one press while the previous
    // cycle of the same order completed normally. Cleared by STOP / stop plan.
    private int? _armedOrderId;
    private readonly Lock _armSync = new();

    public RobotCommandService(
        IOptions<RobotWorkerOptions> options,
        SnapshotStore snapshots,
        IPlanningReader planning,
        CycleEvents cycleEvents,
        EventLog events,
        TimeProvider time)
    {
        _options = options.Value;
        _snapshots = snapshots;
        _planning = planning;
        _cycleEvents = cycleEvents;
        _events = events;
        _time = time;
    }

    /// <summary>Signalled whenever a command is queued, so the worker reacts without waiting a full cycle.</summary>
    internal WaitHandle WakeHandle => _wake;

    // ---- Non-motion commands -----------------------------------------------------------------

    /// <summary>Software STOP. Always accepted. Does NOT replace the hardware emergency stop.</summary>
    public Task<CommandResult> StopAsync(string source, CancellationToken cancellationToken = default) =>
        SendAsync(new StopCommand(), source, cancellationToken: cancellationToken);

    public Task<CommandResult> ConnectAsync(string source) => SendAsync(new ConnectCommand(), source);
    public Task<CommandResult> DisconnectAsync(string source) => SendAsync(new DisconnectCommand(), source);
    public Task<CommandResult> EnableAsync(string source) => SendAsync(new EnableCommand(), source);
    public Task<CommandResult> ClearFaultAsync(string source) => SendAsync(new ClearFaultCommand(), source);
    public Task<CommandResult> PausePlanAsync(string source) => SendAsync(new PausePlanCommand(), source);
    public Task<CommandResult> StopPlanAsync(string source) => SendAsync(new StopPlanCommand(), source);

    /// <summary>
    /// Queues a non-motion command and waits for its result. Motion commands are refused here.
    /// On timeout or cancellation a command that has not started yet is withdrawn and never executes.
    /// </summary>
    /// <param name="source">Who sends the command (page, user, API client), for the event log.</param>
    public Task<CommandResult> SendAsync(RobotCommand command, string source, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (command.IsMotion)
        {
            Log(EventLevel.Warning, $"{command.Name} refused from {source}: motion commands require confirmation.");
            return Task.FromResult(CommandResult.Rejected("Motion commands require confirmation."));
        }
        if (command is StopCommand or StopPlanCommand)
        {
            Disarm();
            VoidConfirmations(command.Name);
        }
        return EnqueueAsync(command, source, timeout, cancellationToken);
    }

    // ---- Motion commands: two-step confirmation ----------------------------------------------

    /// <summary>
    /// Step 1 for a cycle: the job of <paramref name="productCode"/>, counted for <paramref name="orderId"/>
    /// when given (the order code wins). Reads the current job version: edits apply from the next cycle.
    /// </summary>
    public async Task<ConfirmationRequest> RequestStartAsync(string productCode, int? orderId, string source, CancellationToken ct = default)
    {
        var (request, error) = await BuildCycleRequestAsync(productCode, orderId, ct);
        if (request is null)
            return Reject($"Start '{ProductCode.Normalize(productCode)}'", error!, source);
        return RequestConfirmation(new StartCycleCommand(request), source);
    }

    /// <summary>Step 1 for resuming a paused plan.</summary>
    public ConfirmationRequest RequestResume(string source) => RequestConfirmation(new ResumePlanCommand(), source);

    /// <summary>
    /// True when the next cycle of <paramref name="orderId"/> can start with a single press: its first
    /// cycle was confirmed and the last cycle (of this order) completed normally.
    /// </summary>
    public bool CanRepeatWithoutConfirmation(int orderId)
    {
        lock (_armSync)
        {
            var snapshot = _snapshots.Current;
            return _armedOrderId == orderId
                && snapshot.ActiveCycle is null
                && snapshot.LastCycle is { Outcome: CycleOutcome.Completed } last
                && last.OrderId == orderId;
        }
    }

    /// <summary>Next cycle of an armed order, without a second confirmation. Same checks as a first start.</summary>
    public async Task<CommandResult> StartNextCycleAsync(int orderId, string source, CancellationToken ct = default)
    {
        if (!CanRepeatWithoutConfirmation(orderId))
            return CommandResult.Rejected("Confirmation required for this start.");
        var (request, error) = await BuildCycleRequestAsync("", orderId, ct);
        if (request is null)
        {
            Log(EventLevel.Warning, $"Next cycle refused from {source}: {error}");
            return CommandResult.Rejected(error!);
        }
        var command = new StartCycleCommand(request);
        Log(EventLevel.Info, $"Next cycle requested by {source}: {command.Describe()}.");
        return await EnqueueAsync(command, source, null, ct);
    }

    /// <summary>
    /// Step 1: validates a motion command against the current state and returns a single-use token.
    /// Nothing is sent to the robot until <see cref="ConfirmAsync"/>.
    /// </summary>
    public ConfirmationRequest RequestConfirmation(RobotCommand command, string source)
    {
        var summary = command.Describe();
        if (!command.IsMotion)
            return Reject(summary, $"{command.Name} does not need confirmation.", source);

        var rejection = CommandPolicy.Check(_snapshots.Current.State, command);
        if (rejection is not null)
            return Reject(summary, rejection, source);

        PurgeExpired();
        var now = _time.GetUtcNow();
        var expiresAt = now + _options.ConfirmationTimeout;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _confirmations[token] = new PendingConfirmation(command, summary, source, expiresAt);

        Log(EventLevel.Info, $"Confirmation requested by {source}: {summary} (expires in {_options.ConfirmationTimeout.TotalSeconds:0} s).");
        return new ConfirmationRequest(token, null, summary, expiresAt);
    }

    private async Task<(CycleRequest? Request, string? Error)> BuildCycleRequestAsync(string productCode, int? orderId, CancellationToken ct)
    {
        // Let the recorder store the previous cycle first, so order counts are up to date.
        if (!await _cycleEvents.WaitIdleAsync(TimeSpan.FromSeconds(2), ct))
            return (null, "Cycle history is still being saved: retry.");

        ProductionOrder? order = null;
        if (orderId is { } id)
        {
            order = await _planning.GetOrderAsync(id, ct);
            if (order is null || !order.IsOpen)
                return (null, "The order is no longer open.");
            if (order.Remaining == 0)
                return (null, $"Order {order.OrderNumber} is already complete.");
            productCode = order.ProductCode;
        }

        var job = await _planning.FindJobByCodeAsync(productCode, ct);
        if (job is null)
            return (null, $"No job for product code '{ProductCode.Normalize(productCode)}'.");
        if (!job.Enabled)
            return (null, $"Job for '{job.ProductCode}' is disabled.");
        if (job.VelocityScale > _options.MaxVelocityScale)
            return (null, $"Velocity {job.VelocityScale} % exceeds the limit of {_options.MaxVelocityScale} %.");
        if (!_snapshots.Current.AvailablePlans.Contains(job.PlanName))
            return (null, $"Plan '{job.PlanName}' is not stored on the robot.");

        var request = new CycleRequest(job, order?.Id, order?.OrderNumber);
        if (CommandPolicy.Check(_snapshots.Current.State, new StartCycleCommand(request)) is { } rejection)
            return (null, rejection);
        return (request, null);
    }

    private void Disarm()
    {
        lock (_armSync) _armedOrderId = null;
    }

    /// <summary>Step 2: executes the confirmed command. The token is consumed even if the command fails.</summary>
    public Task<CommandResult> ConfirmAsync(string token, string source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token) || !_confirmations.TryRemove(token, out var pending))
        {
            Log(EventLevel.Warning, $"Confirmation from {source} refused: unknown, used or voided token.");
            return Task.FromResult(CommandResult.Rejected("Unknown, already used or voided confirmation."));
        }
        if (_time.GetUtcNow() > pending.ExpiresAt)
        {
            Log(EventLevel.Warning, $"Confirmation from {source} refused: expired ({pending.Summary}).");
            return Task.FromResult(CommandResult.Rejected("Confirmation expired: request the command again."));
        }

        Log(EventLevel.Info, $"Confirmed by {source}: {pending.Summary}.");
        return ConfirmedAsync(pending.Command, source, cancellationToken);
    }

    private async Task<CommandResult> ConfirmedAsync(RobotCommand command, string source, CancellationToken ct)
    {
        var result = await EnqueueAsync(command, source, null, ct);
        if (result.IsSuccess && command is StartCycleCommand { Request.OrderId: { } orderId })
            lock (_armSync) _armedOrderId = orderId;
        return result;
    }

    /// <summary>Drops a pending confirmation (operator pressed Cancel).</summary>
    public bool CancelConfirmation(string token, string source)
    {
        if (string.IsNullOrEmpty(token) || !_confirmations.TryRemove(token, out var pending))
            return false;
        Log(EventLevel.Info, $"Confirmation cancelled by {source}: {pending.Summary}.");
        return true;
    }

    private ConfirmationRequest Reject(string summary, string reason, string source)
    {
        Log(EventLevel.Warning, $"Request refused from {source}: {summary}: {reason}");
        return new ConfirmationRequest(null, reason, summary, _time.GetUtcNow());
    }

    private void VoidConfirmations(string reason)
    {
        foreach (var token in _confirmations.Keys)
        {
            if (_confirmations.TryRemove(token, out var pending))
                Log(EventLevel.Info, $"Pending confirmation voided by {reason}: {pending.Summary}.");
        }
    }

    private void PurgeExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var (token, pending) in _confirmations)
        {
            if (now > pending.ExpiresAt)
                _confirmations.TryRemove(token, out _);
        }
    }

    // ---- Queueing ----------------------------------------------------------------------------

    private async Task<CommandResult> EnqueueAsync(RobotCommand command, string source, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var pending = new PendingCommand(command, source);
        var queue = command is StopCommand ? _stopQueue : _normalQueue;
        if (!queue.Writer.TryWrite(pending))
            return CommandResult.Rejected("Command queue is full.");
        _wake.Set();

        try
        {
            return await pending.Completion.WaitAsync(timeout ?? _options.DefaultCommandTimeout, cancellationToken).ConfigureAwait(false);
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

    private void Log(EventLevel level, string message) =>
        _events.Add(new RobotEvent(_time.GetUtcNow(), level, "Commands", message));

    public void Dispose() => _wake.Dispose();

    private sealed record PendingConfirmation(RobotCommand Command, string Summary, string Source, DateTimeOffset ExpiresAt);
}

/// <summary>A queued command with its completion and a queued/started/cancelled flag.</summary>
internal sealed class PendingCommand(RobotCommand command, string source)
{
    private const int Queued = 0, Started = 1, Withdrawn = 2;
    private int _state = Queued;
    private readonly TaskCompletionSource<CommandResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RobotCommand Command { get; } = command;
    public string Source { get; } = source;
    public Task<CommandResult> Completion => _tcs.Task;

    /// <summary>Worker side: claims the command; false if the caller already withdrew it.</summary>
    public bool TryStart() => Interlocked.CompareExchange(ref _state, Started, Queued) == Queued;

    /// <summary>Caller side: withdraws the command if the worker has not started it.</summary>
    public bool TryCancel() => Interlocked.CompareExchange(ref _state, Withdrawn, Queued) == Queued;

    public void Complete(CommandResult result) => _tcs.TrySetResult(result);
}
