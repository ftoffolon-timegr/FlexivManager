using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RobotGateway.Core;

/// <summary>
/// The only owner of <see cref="IRobotBackend"/>. Runs a dedicated thread that, every cycle:
/// executes queued commands (STOP first), polls the backend, derives the service state and
/// publishes an immutable <see cref="RobotSnapshot"/>. It never enables or moves the robot on its own.
/// </summary>
public sealed class RobotWorker(
    IRobotBackend backend,
    RobotCommandService commands,
    SnapshotStore store,
    EventLog events,
    IOptions<RobotWorkerOptions> options,
    TimeProvider time,
    ILogger<RobotWorker> logger) : BackgroundService
{
    private readonly RobotWorkerOptions _options = options.Value;

    // State below is touched only by the worker thread.
    private RobotServiceState _state = RobotServiceState.Disconnected;
    private BackendStatus _status;
    private RobotStates? _states;
    private PlanInfo? _plan;
    private bool _pauseLatched;
    private bool _manualDisconnect;
    private DateTimeOffset _nextConnectAttempt = DateTimeOffset.MinValue;
    private ImmutableArray<string> _plans = [];
    private string? _currentPlan;
    private DateTimeOffset? _cycleStartedAt;
    private long _completedCycles;
    private TimeSpan? _lastCycleTime;
    private string? _lastError;
    private long _sequence;
    private double _loopRateHz;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Run(stoppingToken);
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Robot worker crashed.");
                done.TrySetException(ex);
            }
        })
        {
            Name = "RobotWorker",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();
        return done.Task;
    }

    private void Run(CancellationToken ct)
    {
        var periodTicks = (long)(Stopwatch.Frequency / _options.PollRateHz);
        var waitHandles = new[] { commands.WakeHandle, ct.WaitHandle };
        var nextDeadline = Stopwatch.GetTimestamp();
        var rateWindowStart = nextDeadline;
        var rateWindowCycles = 0;

        AddEvent(EventLevel.Info, $"Worker started: backend {backend.Name}, {_options.PollRateHz:0} Hz. No motion is started automatically.");

        while (!ct.IsCancellationRequested)
        {
            ProcessCommands();
            TryAutoConnect();
            Poll();
            Publish();

            // Absolute deadlines: Windows wait granularity (~15.6 ms) adds jitter but the average rate holds.
            nextDeadline += periodTicks;
            var now = Stopwatch.GetTimestamp();
            if (now - nextDeadline > periodTicks)
                nextDeadline = now; // fell behind: skip, never burst
            var remainingTicks = nextDeadline - now;
            if (remainingTicks > 0)
            {
                var signalled = WaitHandle.WaitAny(waitHandles, TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency));
                if (signalled == 0)
                    nextDeadline = Stopwatch.GetTimestamp(); // woken by a command: run now, restart schedule
            }

            rateWindowCycles++;
            var windowTicks = Stopwatch.GetTimestamp() - rateWindowStart;
            if (windowTicks >= Stopwatch.Frequency)
            {
                _loopRateHz = rateWindowCycles * (double)Stopwatch.Frequency / windowTicks;
                rateWindowStart += windowTicks;
                rateWindowCycles = 0;
            }
        }

        Shutdown();
    }

    // ---- Commands ----------------------------------------------------------------------------

    private void ProcessCommands()
    {
        while (commands.TryDequeueStop(out var stop))
            Handle(stop);

        // One normal command per cycle, so each one is checked against a freshly polled state.
        if (commands.TryDequeueNormal(out var next))
            Handle(next);
    }

    private void Handle(PendingCommand pending)
    {
        if (!pending.TryStart())
            return; // withdrawn by the caller (timeout/cancel): never execute late

        var command = pending.Command;
        var result = command is StopCommand ? ExecuteStop() : Execute(command);

        var level = result.IsSuccess ? EventLevel.Info : EventLevel.Warning;
        AddEvent(level, $"Command {command.Name}: {result.Outcome}{(result.Message is null ? "" : " - " + result.Message)}");
        pending.Complete(result);
    }

    private CommandResult ExecuteStop()
    {
        var flushed = commands.FlushNormal("Superseded by STOP.");
        _pauseLatched = false;
        _cycleStartedAt = null;
        try
        {
            if (!backend.IsConnected)
                return CommandResult.Ok("Not connected: nothing to stop.");
            backend.Stop();
            return CommandResult.Ok(flushed > 0 ? $"{flushed} queued command(s) cancelled." : null);
        }
        catch (Exception ex)
        {
            if (ex is RobotBackendException) HandleLinkLoss(ex.Message);
            return CommandResult.Failed($"STOP failed: {ex.Message}");
        }
    }

    private CommandResult Execute(RobotCommand command)
    {
        var rejection = CommandPolicy.Check(_state, command);
        if (rejection is not null)
            return CommandResult.Rejected(rejection);

        try
        {
            switch (command)
            {
                case ConnectCommand:
                    _manualDisconnect = false;
                    backend.Connect();
                    OnConnected();
                    break;
                case DisconnectCommand:
                    _manualDisconnect = true;
                    backend.Disconnect();
                    _status = BackendStatus.Disconnected;
                    break;
                case EnableCommand:
                    backend.Enable();
                    break;
                case ClearFaultCommand:
                    backend.ClearFault();
                    break;
                case SwitchModeCommand c:
                    backend.SwitchMode(c.Mode);
                    break;
                case SetVelocityScaleCommand c:
                    backend.SetVelocityScale(c.Percent);
                    break;
                case ExecutePlanCommand c:
                    return ExecutePlan(c.PlanName);
                case PausePlanCommand:
                    backend.PausePlan(true);
                    _pauseLatched = true;
                    break;
                case ResumePlanCommand:
                    backend.PausePlan(false);
                    _pauseLatched = false;
                    break;
                case StopPlanCommand:
                    backend.StopPlan();
                    _pauseLatched = false;
                    _cycleStartedAt = null;
                    break;
                default:
                    return CommandResult.Rejected($"Unsupported command {command.Name}.");
            }
            return CommandResult.Ok();
        }
        catch (RobotBackendException ex)
        {
            HandleLinkLoss(ex.Message);
            return CommandResult.Failed(ex.Message);
        }
        catch (Exception ex)
        {
            return CommandResult.Failed(ex.Message);
        }
    }

    private CommandResult ExecutePlan(string planName)
    {
        if (!_plans.Contains(planName))
        {
            _plans = [.. backend.GetPlanList()];
            if (!_plans.Contains(planName))
                return CommandResult.Rejected($"Plan '{planName}' not found on the robot.");
        }

        // Plan execution requires NRT_PLAN_EXECUTION mode; switching mode does not move the robot.
        if (backend.Mode != RobotMode.NrtPlanExecution)
            backend.SwitchMode(RobotMode.NrtPlanExecution);

        backend.ExecutePlan(planName, continueExec: false);
        _pauseLatched = false;
        _currentPlan = planName;
        _cycleStartedAt = time.GetUtcNow();
        return CommandResult.Ok();
    }

    // ---- Connection --------------------------------------------------------------------------

    private void TryAutoConnect()
    {
        if (!_options.AutoConnect || _manualDisconnect || backend.IsConnected)
            return;
        var now = time.GetUtcNow();
        if (now < _nextConnectAttempt)
            return;
        _nextConnectAttempt = now + _options.ReconnectInterval;

        try
        {
            backend.Connect();
            OnConnected();
        }
        catch (Exception ex)
        {
            var message = $"Connect failed: {ex.Message}";
            if (message != _lastError)
                AddEvent(EventLevel.Warning, message);
            _lastError = message;
        }
    }

    private void OnConnected()
    {
        _lastError = null;
        _plans = [.. backend.GetPlanList()];
        AddEvent(EventLevel.Info, $"Connected to {backend.Name}: {_plans.Length} plan(s) available.");
    }

    private void HandleLinkLoss(string reason)
    {
        _lastError = $"Link lost: {reason}";
        _pauseLatched = false;
        _status = BackendStatus.Disconnected;
        _nextConnectAttempt = time.GetUtcNow() + _options.ReconnectInterval;
        AddEvent(EventLevel.Error, _lastError);
        try { backend.Disconnect(); }
        catch (Exception ex) { logger.LogDebug(ex, "Disconnect after link loss failed."); }
    }

    // ---- Polling -----------------------------------------------------------------------------

    private void Poll()
    {
        var status = BackendStatus.Disconnected;
        RobotStates? states = null;
        PlanInfo? plan = null;

        if (backend.IsConnected)
        {
            try
            {
                status = new BackendStatus(
                    Connected: true,
                    Operational: backend.IsOperational,
                    OperationalStatus: backend.OperationalStatus,
                    Fault: backend.IsFault,
                    EStopReleased: backend.IsEStopReleased,
                    Busy: backend.IsBusy,
                    Mode: backend.Mode);
                states = backend.ReadStates();
                if (status.Mode == RobotMode.NrtPlanExecution)
                    plan = backend.GetPlanInfo();
                foreach (var e in backend.DrainEventLog())
                    events.Add(e);
            }
            catch (Exception ex)
            {
                HandleLinkLoss(ex.Message);
                status = BackendStatus.Disconnected;
                states = null;
                plan = null;
            }
        }
        else if (_status.Connected)
        {
            HandleLinkLoss("backend reports disconnected");
        }

        if (!RobotStateMachine.CanStayPaused(status))
            _pauseLatched = false;

        var newState = RobotStateMachine.Derive(status, _pauseLatched);
        if (newState != _state)
            OnTransition(_state, newState);

        _state = newState;
        _status = status;
        _states = states;
        _plan = plan;
    }

    private void OnTransition(RobotServiceState from, RobotServiceState to)
    {
        if (from == RobotServiceState.Running && to == RobotServiceState.Operational && _cycleStartedAt is { } started)
        {
            _completedCycles++;
            _lastCycleTime = time.GetUtcNow() - started;
        }
        if (to is not (RobotServiceState.Running or RobotServiceState.Paused))
        {
            _cycleStartedAt = null;
            _currentPlan = null;
        }

        var level = to is RobotServiceState.Fault or RobotServiceState.EStop or RobotServiceState.Disconnected
            ? EventLevel.Warning
            : EventLevel.Info;
        AddEvent(level, $"State {from} -> {to}");
    }

    private void Publish()
    {
        store.Publish(new RobotSnapshot
        {
            Sequence = ++_sequence,
            Timestamp = time.GetUtcNow(),
            BackendName = backend.Name,
            State = _state,
            Status = _status,
            States = _states,
            Plan = _plan,
            CurrentPlan = _state is RobotServiceState.Running or RobotServiceState.Paused ? _currentPlan : null,
            AvailablePlans = _plans,
            CompletedCycles = _completedCycles,
            LastCycleTime = _lastCycleTime,
            LastError = _lastError,
            LoopRateHz = _loopRateHz,
        });
    }

    // ---- Shutdown ----------------------------------------------------------------------------

    private void Shutdown()
    {
        try
        {
            if (backend.IsConnected)
            {
                if (_state is RobotServiceState.Running or RobotServiceState.Paused)
                    backend.Stop();
                backend.Disconnect();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error while stopping the robot at shutdown.");
        }

        while (commands.TryDequeueStop(out var p) || commands.TryDequeueNormal(out p))
        {
            if (p.TryStart())
                p.Complete(CommandResult.Cancelled("Worker is shutting down."));
        }

        _state = RobotServiceState.Disconnected;
        _status = BackendStatus.Disconnected;
        _states = null;
        _plan = null;
        Publish();
        AddEvent(EventLevel.Info, "Worker stopped.");
    }

    private void AddEvent(EventLevel level, string message)
    {
        events.Add(new RobotEvent(time.GetUtcNow(), level, "Gateway", message));
        var logLevel = level switch
        {
            EventLevel.Error => LogLevel.Error,
            EventLevel.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        logger.Log(logLevel, "{Message}", message);
    }
}
