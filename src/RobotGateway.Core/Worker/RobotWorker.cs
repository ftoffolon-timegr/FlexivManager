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
    private const int PlanVelocityScale = 100;

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
    private string? _robotFault;
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

        AddEvent(EventLevel.Info, $"Servizio robot avviato: backend {backend.Name}, {_options.PollRateHz:0} Hz. Nessun movimento parte in automatico.");

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
        AddEvent(level, $"Comando {command.Label} da {pending.Source}: {Labels.Of(result.Outcome)}{(result.Message is null ? "" : " - " + result.Message)}");
        pending.Complete(result);
    }

    private CommandResult ExecuteStop(string source)
    {
        var flushed = commands.FlushNormal("Sostituito dallo STOP.");
        _pauseLatched = false;
        FinishCycle(CycleOutcome.Stopped, $"STOP da {source}");
        try
        {
            if (!backend.IsConnected)
                return CommandResult.Ok("Non connesso: niente da fermare.");
            backend.Stop();
            return CommandResult.Ok(flushed > 0 ? $"{flushed} comandi in coda annullati." : null);
        }
        catch (Exception ex)
        {
            if (ex is RobotBackendException) HandleLinkLoss(ex.Message);
            return CommandResult.Failed($"STOP fallito: {ex.Message}");
        }
    }

    private CommandResult Execute(RobotCommand command, string source)
    {
        var rejection = CommandPolicy.Check(_state, command);
        if (rejection is not null)
            return CommandResult.Rejected(rejection);
        if (command is StartCycleCommand && _cycle is not null)
            return CommandResult.Rejected("Un ciclo è già in esecuzione.");

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
                    FinishCycle(CycleOutcome.Stopped, $"Fermato da {source}");
                    break;
                default:
                    return CommandResult.Rejected($"Comando non supportato: {command.Label}.");
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
                return CommandResult.Rejected($"Il plan '{job.PlanName}' non è presente sul robot.");
        }

        // Plan execution requires NRT_PLAN_EXECUTION mode; switching mode does not move the robot.
        // It must come first: the RDK refuses SetVelocityScale outside the NRT plan/primitive modes.
        if (backend.Mode != RobotMode.NrtPlanExecution)
            backend.SwitchMode(RobotMode.NrtPlanExecution);
        // The plan must run exactly as programmed in Elements. The robot velocity override is global and
        // persistent (Elements, pendant), so it is reset to 100 % before every plan instead of inheriting it.
        backend.SetVelocityScale(PlanVelocityScale);
        backend.ExecutePlan(job.PlanName, continueExec: false);

        _pauseLatched = false;
        _cycleSeenRunning = false;
        _cycle = new ActiveCycle(job.ProductCode, job.PlanName, request.OrderId, request.OrderNumber, source, time.GetUtcNow());
        cycleEvents.Publish(new CycleStarted(_cycle));
        AddEvent(EventLevel.Info, $"Ciclo avviato: plan {job.PlanName}, codice '{job.ProductCode}'"
            + (request.OrderId is null ? " (servizio)." : request.OrderNumber is { Length: > 0 } number ? $", ordine {number}." : ", ordine di oggi."));
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
                    FinishCycle(CycleOutcome.Failed, "Il plan non è partito.");
                break;
            default:
                FinishCycle(CycleOutcome.Failed, $"{Labels.Of(_state)} durante il ciclo" + (_robotFault is null ? "." : $": {_robotFault}"));
                break;
        }
    }

    private void FinishCycle(CycleOutcome outcome, string? message)
    {
        if (_cycle is not { } cycle) return;
        var now = time.GetUtcNow();
        _lastCycle = new CycleRecord(cycle.ProductCode, cycle.PlanName, cycle.OrderId, cycle.OrderNumber,
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
        AddEvent(level, $"Ciclo {Labels.Of(outcome)}: plan {cycle.PlanName}, {_lastCycle.Duration.TotalSeconds:0.0} s"
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
            var message = $"Connessione fallita: {ex.Message}";
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
        AddEvent(EventLevel.Info, $"Connesso a {backend.Name}: {_plans.Length} plan disponibili.");
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
                AddEvent(EventLevel.Info, $"Elenco dei plan del robot cambiato: {plans.Length} plan.");
            _plans = plans;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Plan list refresh failed.");
        }
    }

    private void HandleLinkLoss(string reason)
    {
        _lastError = $"Collegamento perso: {reason}";
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
                {
                    events.Add(e);
                    if (e.Level == EventLevel.Error)
                        _robotFault = RobotFaultText.Extract(e.Message);
                }
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
            HandleLinkLoss("il backend risulta disconnesso");
        }

        if (!RobotStateMachine.CanStayPaused(status))
            _pauseLatched = false;

        // Auto (Remote) is selected on the robot, not by this service: log when it is lost or regained.
        if (status.RemoteMode != _status.RemoteMode && status.RemoteMode != RemoteMode.Unknown)
            AddEvent(status.RemoteMode == RemoteMode.Remote ? EventLevel.Info : EventLevel.Warning,
                $"Modalità del robot: {Labels.Of(status.OperationalStatus)}.");

        var newState = RobotStateMachine.Derive(status, _pauseLatched);
        if (newState != _state)
        {
            var level = newState is RobotServiceState.Fault or RobotServiceState.EStop or RobotServiceState.Disconnected
                ? EventLevel.Warning
                : EventLevel.Info;
            AddEvent(level, $"Stato {Labels.Of(_state)} -> {Labels.Of(newState)}");
            // The fault is over (cleared, or link lost): its message no longer applies.
            if (_state is RobotServiceState.Fault or RobotServiceState.EStop
                && newState is not (RobotServiceState.Fault or RobotServiceState.EStop))
                _robotFault = null;
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
            RobotFault = _robotFault,
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
        FinishCycle(CycleOutcome.Stopped, "Arresto del servizio.");

        while (commands.TryDequeueStop(out var p) || commands.TryDequeueNormal(out p))
        {
            if (p.TryStart())
                p.Complete(CommandResult.Cancelled("Il servizio robot si sta arrestando."));
        }

        _state = RobotServiceState.Disconnected;
        _status = BackendStatus.Disconnected;
        _states = null;
        _plan = null;
        Publish();
        AddEvent(EventLevel.Info, "Servizio robot arrestato.");
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
