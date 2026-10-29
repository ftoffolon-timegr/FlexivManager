using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RobotGateway.Core;

/// <summary>
/// The only owner of <see cref="IRobotBackend"/>. Runs a dedicated thread that, every cycle:
/// executes queued commands (STOP first), polls the backend, derives the service state, tracks the
/// running work cycle and publishes an immutable <see cref="RobotSnapshot"/>.
/// It never enables or moves the robot on its own.
/// </summary>
public sealed class RobotWorker(
    IRobotBackend backend,
    RobotCommandService commands,
    SnapshotStore store,
    EventLog events,
    CycleEvents cycleEvents,
    IOptions<RobotWorkerOptions> options,
    TimeProvider time,
    ILogger<RobotWorker> logger) : BackgroundService
{
    private const int WorkerStackSize = 16 * 1024 * 1024;

    private readonly RobotWorkerOptions _options = options.Value;

    // State below is touched only by the worker thread.
    private RobotServiceState _state = RobotServiceState.Disconnected;
    private BackendStatus _status;
    private RobotStates? _states;
    private PlanInfo? _plan;
    private bool _pauseLatched;
    private bool _manualDisconnect;
    private DateTimeOffset _nextConnectAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextPlanListRefresh = DateTimeOffset.MaxValue;
    private ImmutableArray<string> _plans = [];
    private ActiveCycle? _cycle;
    private bool _cycleSeenRunning;
    private CycleRecord? _lastCycle;
    private long _completedCycles;
    private string? _lastError;
    private long _sequence;
    private double _loopRateHz;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Large stack: the native Flexiv RDK overflows the default ~1.5 MB while constructing the robot object
        // (seen with RDK 1.8 on 2026-10-29), and it then runs on this thread for the whole session.
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
        }, maxStackSize: WorkerStackSize)
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
            TrackCycle();
            RefreshPlanList();
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
        var result = command is StopCommand ? ExecuteStop(pending.Source) : Execute(command, pending.Source);

        var level = result.IsSuccess ? EventLevel.Info : EventLevel.Warning;
        AddEvent(level, $"Command {command.Name} from {pending.Source}: {result.Outcome}{(result.Message is null ? "" : " - " + result.Message)}");
        pending.Complete(result);
    }

    private CommandResult ExecuteStop(string source)
    {
        var flushed = commands.FlushNormal("Superseded by STOP.");
        _pauseLatched = false;
        FinishCycle(CycleOutcome.Stopped, $"STOP from {source}");
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

    private CommandResult Execute(RobotCommand command, string source)
    {
        var rejection = CommandPolicy.Check(_state, command);
        if (rejection is not null)
            return CommandResult.Rejected(rejection);
        if (command is StartCycleCommand && _cycle is not null)
            return CommandResult.Rejected("A cycle is already running.");

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
                case StartCycleCommand c:
                    return StartCycle(c.Request, source);
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
                    FinishCycle(CycleOutcome.Stopped, $"Stopped by {source}");
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

    private CommandResult StartCycle(CycleRequest request, string source)
    {
        var job = request.Job;
        if (!_plans.Contains(job.PlanName))
        {
            _plans = [.. backend.GetPlanList()];
            if (!_plans.Contains(job.PlanName))
                return CommandResult.Rejected($"Plan '{job.PlanName}' not found on the robot.");
        }

        backend.SetVelocityScale(job.VelocityScale);
        // Plan execution requires NRT_PLAN_EXECUTION mode; switching mode does not move the robot.
        if (backend.Mode != RobotMode.NrtPlanExecution)
            backend.SwitchMode(RobotMode.NrtPlanExecution);
        backend.ExecutePlan(job.PlanName, continueExec: false);

        _pauseLatched = false;
        _cycleSeenRunning = false;
        _cycle = new ActiveCycle(job.ProductCode, job.PlanName, job.VelocityScale, request.OrderId, request.OrderNumber, source, time.GetUtcNow());
        cycleEvents.Publish(new CycleStarted(_cycle));
        AddEvent(EventLevel.Info, $"Cycle started: plan {job.PlanName} @ {job.VelocityScale} %, code '{job.ProductCode}'"
            + (request.OrderNumber is null ? " (one-off)." : $", order {request.OrderNumber}."));
        return CommandResult.Ok();
    }

    // ---- Cycle tracking ----------------------------------------------------------------------

    /// <summary>Ends the running cycle from the polled state: plan done, fault, E-stop, link loss.</summary>
    private void TrackCycle()
    {
        if (_cycle is null) return;
        switch (_state)
        {
            case RobotServiceState.Running or RobotServiceState.Paused:
                _cycleSeenRunning = true;
                break;
            case RobotServiceState.Operational when _cycleSeenRunning:
                FinishCycle(CycleOutcome.Completed, null);
                break;
            case RobotServiceState.Operational:
                // The robot may report busy a little after ExecutePlan: wait, but not forever.
                if (time.GetUtcNow() - _cycle.StartedAt > _options.PlanStartTimeout)
                    FinishCycle(CycleOutcome.Failed, "Plan did not start.");
                break;
            default:
                FinishCycle(CycleOutcome.Failed, $"{_state} during the cycle.");
                break;
        }
    }

    private void FinishCycle(CycleOutcome outcome, string? message)
    {
        if (_cycle is not { } cycle) return;
        var now = time.GetUtcNow();
        _lastCycle = new CycleRecord(cycle.ProductCode, cycle.PlanName, cycle.VelocityScale, cycle.OrderId, cycle.OrderNumber,
            cycle.Source, cycle.StartedAt, now - cycle.StartedAt, outcome, message);
        _cycle = null;
        if (outcome == CycleOutcome.Completed)
            _completedCycles++;

        cycleEvents.Publish(new CycleFinished(_lastCycle));
        var level = outcome switch
        {
            CycleOutcome.Completed => EventLevel.Info,
            CycleOutcome.Stopped => EventLevel.Warning,
            _ => EventLevel.Error,
        };
        AddEvent(level, $"Cycle {outcome}: plan {cycle.PlanName}, {_lastCycle.Duration.TotalSeconds:0.0} s"
            + (message is null ? "." : $" - {message}"));
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
        _nextPlanListRefresh = time.GetUtcNow() + _options.PlanListRefreshInterval;
        AddEvent(EventLevel.Info, $"Connected to {backend.Name}: {_plans.Length} plan(s) available.");
    }

    /// <summary>Re-reads the robot plan list periodically while idle, for the remote editor.</summary>
    private void RefreshPlanList()
    {
        if (_state is not (RobotServiceState.Connected or RobotServiceState.Operational) || _cycle is not null)
            return;
        var now = time.GetUtcNow();
        if (now < _nextPlanListRefresh)
            return;
        _nextPlanListRefresh = now + _options.PlanListRefreshInterval;
        try
        {
            ImmutableArray<string> plans = [.. backend.GetPlanList()];
            if (!plans.SequenceEqual(_plans))
                AddEvent(EventLevel.Info, $"Robot plan list changed: {plans.Length} plan(s).");
            _plans = plans;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Plan list refresh failed.");
        }
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
        {
            var level = newState is RobotServiceState.Fault or RobotServiceState.EStop or RobotServiceState.Disconnected
                ? EventLevel.Warning
                : EventLevel.Info;
            AddEvent(level, $"State {_state} -> {newState}");
        }

        _state = newState;
        _status = status;
        _states = states;
        _plan = plan;
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
            CurrentPlan = _cycle?.PlanName,
            CurrentPlanStartedAt = _cycle?.StartedAt,
            ActiveCycle = _cycle,
            LastCycle = _lastCycle,
            AvailablePlans = _plans,
            CompletedCycles = _completedCycles,
            LastCycleTime = _lastCycle?.Outcome == CycleOutcome.Completed ? _lastCycle.Duration : null,
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
        FinishCycle(CycleOutcome.Stopped, "Service shutting down.");

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
