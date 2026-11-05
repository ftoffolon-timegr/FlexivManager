using RobotGateway.Core;

namespace RobotGateway.Mock;

/// <summary>
/// Simulated 7-axis Rizon-like robot. Time-driven (via <see cref="TimeProvider"/>), so tests can step it.
/// Mimics the RDK flow: connect -> NOT_ENABLED -> Enable -> RELEASING_BRAKE -> operational,
/// plan execution only in NRT_PLAN_EXECUTION mode, Stop() switches to IDLE.
/// Thread-safe (locks), although in production only the worker calls it.
/// </summary>
public sealed class MockBackend : IRobotBackend
{
    public const int JointCount = 7;

    // Rizon-like home posture [rad].
    private static readonly double[] Home = [0, -0.698, 0, 1.571, 0, 0.698, 0];
    private static readonly double[] Amplitude = [0.6, 0.25, 0.4, 0.35, 0.5, 0.3, 0.8];
    private static readonly double[] GravityTorque = [0, 45, 2, 20, 1, 3, 0.2];

    private readonly MockBackendOptions _options;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly Lock _sync = new();
    private readonly List<RobotEvent> _pendingEvents = [];

    private readonly double[] _q = (double[])Home.Clone();
    private readonly double[] _dq = new double[JointCount];
    private readonly double[] _temperature = [32, 34, 33, 35, 31, 30, 29];
    private double[] _planStartQ = (double[])Home.Clone();

    private bool _connected;
    private bool _linkLost;
    private bool _operational;
    private bool _fault;
    private bool _estopPressed;
    private OperationalStatus _status = OperationalStatus.Unknown;
    private RobotMode _mode = RobotMode.Unknown;
    private int _velocityScale = 100;
    private MockPlan? _runningPlan;
    private bool _paused;
    private double _planElapsed; // seconds of plan time, scaled by velocity
    private long? _enableDoneAt;
    private long _lastUpdate;

    public MockBackend(MockBackendOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
        _random = new Random(options.Seed);
        _lastUpdate = time.GetTimestamp();
    }

    public string Name => "Mock";

    // ---- Simulation hooks (tests and demo) ---------------------------------------------------

    /// <summary>Raises a robot fault, as a collision would.</summary>
    public void InjectFault(string reason = "Anomalia simulata")
    {
        lock (_sync)
        {
            Update();
            RaiseFault(reason);
        }
    }

    /// <summary>Presses (true) or releases (false) the simulated emergency stop.</summary>
    public void SetEStop(bool pressed)
    {
        lock (_sync)
        {
            Update();
            if (pressed == _estopPressed) return;
            _estopPressed = pressed;
            if (pressed)
            {
                AbortMotion();
                _operational = false;
                _enableDoneAt = null;
                _status = OperationalStatus.EStopNotReleased;
                AddEvent(EventLevel.Error, "Arresto di emergenza premuto.");
            }
            else
            {
                _status = _fault ? OperationalStatus.MinorFault : OperationalStatus.NotEnabled;
                AddEvent(EventLevel.Info, "Arresto di emergenza rilasciato.");
            }
        }
    }

    /// <summary>Simulates a cable unplug (true) or its restoration (false).</summary>
    public void SimulateLinkLoss(bool lost)
    {
        lock (_sync)
        {
            _linkLost = lost;
            if (lost)
            {
                _connected = false;
                AbortMotion();
                _operational = false;
            }
        }
    }

    // ---- IRobotBackend -----------------------------------------------------------------------

    public void Connect()
    {
        lock (_sync)
        {
            if (_linkLost) throw new RobotBackendException("Robot simulato non raggiungibile.");
            if (_connected) return;
            _connected = true;
            _operational = false;
            _enableDoneAt = null;
            _mode = RobotMode.Idle;
            _status = _estopPressed ? OperationalStatus.EStopNotReleased
                : _fault ? OperationalStatus.MinorFault
                : OperationalStatus.NotEnabled;
            _lastUpdate = _time.GetTimestamp();
            AddEvent(EventLevel.Info, "Client RDK connesso.");
        }
    }

    public void Disconnect()
    {
        lock (_sync)
        {
            if (!_connected) return;
            // A real robot stops when the remote controller goes away.
            AbortMotion();
            _operational = false;
            _connected = false;
        }
    }

    public bool IsConnected
    {
        get { lock (_sync) return _connected && !_linkLost; }
    }

    public bool IsOperational => Read(() => _operational);
    public OperationalStatus OperationalStatus => Read(() => _status);
    public bool IsFault => Read(() => _fault);
    public bool IsEStopReleased => Read(() => !_estopPressed);
    public bool IsBusy => Read(() => _runningPlan is not null);
    public RobotMode Mode => Read(() => _mode);

    public RobotStates ReadStates() => Read(BuildStates);

    public IReadOnlyList<string> GetPlanList() => Read(() => _options.Plans.Select(p => p.Name).ToArray());

    public PlanInfo GetPlanInfo() => Read(() =>
    {
        if (_runningPlan is not { } plan) return PlanInfo.Empty;
        var node = plan.Nodes[CurrentNodeIndex(plan)];
        return new PlanInfo(plan.Name, node, $"rootNode/{node}", _velocityScale, WaitingForStep: false);
    });

    public IReadOnlyList<RobotEvent> DrainEventLog() => Read(() =>
    {
        var drained = _pendingEvents.ToArray();
        _pendingEvents.Clear();
        return drained;
    });

    public void Enable() => Act(() =>
    {
        if (_estopPressed) throw new InvalidOperationException("Arresto di emergenza non rilasciato.");
        if (_fault) throw new InvalidOperationException("Resetta l'anomalia prima di abilitare.");
        if (_operational || _enableDoneAt is not null) return;
        _status = OperationalStatus.ReleasingBrake;
        _enableDoneAt = _time.GetTimestamp() + (long)(_options.EnableDelay.TotalSeconds * _time.TimestampFrequency);
        AddEvent(EventLevel.Info, "Abilitazione: rilascio dei freni.");
    });

    public void ClearFault() => Act(() =>
    {
        if (!_fault) return;
        _fault = false;
        _operational = false;
        _status = _estopPressed ? OperationalStatus.EStopNotReleased : OperationalStatus.NotEnabled;
        AddEvent(EventLevel.Info, "Anomalia resettata.");
    });

    public void SwitchMode(RobotMode mode) => Act(() =>
    {
        if (!_operational) throw new InvalidOperationException("Robot non operativo.");
        if (_runningPlan is not null) throw new InvalidOperationException("Impossibile cambiare modalità durante l'esecuzione.");
        _mode = mode;
    });

    public void SetVelocityScale(int percent) => Act(() =>
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        _velocityScale = percent;
    });

    public void ExecutePlan(string name, bool continueExec = false) => Act(() =>
    {
        if (!_operational) throw new InvalidOperationException("Robot non operativo.");
        if (_mode != RobotMode.NrtPlanExecution) throw new InvalidOperationException("Il robot non è in modalità NRT_PLAN_EXECUTION.");
        if (_runningPlan is not null) throw new InvalidOperationException("Un plan è già in esecuzione.");
        var plan = _options.Plans.FirstOrDefault(p => p.Name == name)
            ?? throw new InvalidOperationException($"Il plan '{name}' non esiste.");
        _runningPlan = plan;
        _paused = false;
        _planElapsed = 0;
        _planStartQ = (double[])_q.Clone();
        AddEvent(EventLevel.Info, $"Plan {name} avviato.");
    });

    public void PausePlan(bool pause) => Act(() =>
    {
        if (_mode != RobotMode.NrtPlanExecution || _runningPlan is null)
            throw new InvalidOperationException("Nessun plan in esecuzione.");
        _paused = pause;
        AddEvent(EventLevel.Info, pause ? "Plan in pausa." : "Plan ripreso.");
    });

    public void StopPlan() => Act(() =>
    {
        if (_runningPlan is not null)
            AddEvent(EventLevel.Info, $"Plan {_runningPlan.Name} fermato.");
        AbortMotion();
    });

    public void Stop() => Act(() =>
    {
        AbortMotion();
        _mode = RobotMode.Idle;
    });

    public void Dispose() { }

    // ---- Simulation --------------------------------------------------------------------------

    private T Read<T>(Func<T> read)
    {
        lock (_sync)
        {
            EnsureConnected();
            Update();
            return read();
        }
    }

    private void Act(Action action)
    {
        lock (_sync)
        {
            EnsureConnected();
            Update();
            action();
        }
    }

    private void EnsureConnected()
    {
        if (_linkLost) throw new RobotBackendException("Collegamento simulato perso.");
        if (!_connected) throw new RobotBackendException("Non connesso.");
    }

    /// <summary>Advances the simulation to the current time.</summary>
    private void Update()
    {
        var now = _time.GetTimestamp();
        var dt = (now - _lastUpdate) / (double)_time.TimestampFrequency;
        _lastUpdate = now;
        if (dt <= 0) return;

        if (_enableDoneAt is { } doneAt && now >= doneAt)
        {
            _enableDoneAt = null;
            _operational = true;
            _status = OperationalStatus.Ready;
            AddEvent(EventLevel.Info, "Robot abilitato e operativo.");
        }

        var previousQ = (double[])_q.Clone();
        if (_runningPlan is { } plan && !_paused)
        {
            _planElapsed += dt * _velocityScale / 100.0;
            if (plan.FaultAfter is { } faultAfter && _planElapsed >= faultAfter.TotalSeconds)
            {
                RaiseFault($"Collisione rilevata durante {plan.Name}.");
            }
            else if (_planElapsed >= plan.Duration.TotalSeconds)
            {
                SetTrajectory(plan, plan.Duration.TotalSeconds);
                _runningPlan = null;
                AddEvent(EventLevel.Info, $"Plan {plan.Name} terminato.");
            }
            else
            {
                SetTrajectory(plan, _planElapsed);
            }
        }

        for (var i = 0; i < JointCount; i++)
        {
            _dq[i] = (_q[i] - previousQ[i]) / dt;
            var target = _runningPlan is not null && !_paused ? 45.0 : 30.0;
            _temperature[i] += (target + i * 0.5 - _temperature[i]) * Math.Min(1, dt / 600.0);
        }
    }

    /// <summary>Smooth periodic motion around the plan's start posture; returns there at the end.</summary>
    private void SetTrajectory(MockPlan plan, double t)
    {
        var phase = 2 * Math.PI * t / plan.Duration.TotalSeconds;
        for (var i = 0; i < JointCount; i++)
            _q[i] = _planStartQ[i] + Amplitude[i] * Math.Sin(phase * (1 + i % 2)) * Math.Sin(phase / 2);
    }

    private int CurrentNodeIndex(MockPlan plan)
    {
        var fraction = Math.Clamp(_planElapsed / plan.Duration.TotalSeconds, 0, 0.9999);
        return (int)(fraction * plan.Nodes.Length);
    }

    private void RaiseFault(string reason)
    {
        AbortMotion();
        _fault = true;
        _operational = false;
        _enableDoneAt = null;
        _status = OperationalStatus.MinorFault;
        AddEvent(EventLevel.Error, reason);
    }

    private void AbortMotion()
    {
        _runningPlan = null;
        _paused = false;
        Array.Clear(_dq);
    }

    private RobotStates BuildStates()
    {
        var q = new double[JointCount];
        var tau = new double[JointCount];
        var tauExt = new double[JointCount];
        for (var i = 0; i < JointCount; i++)
        {
            q[i] = _q[i] + Noise(1e-5);
            tau[i] = GravityTorque[i] * Math.Cos(_q[i]) + Noise(0.05);
            tauExt[i] = Noise(0.2);
        }
        var wrench = new double[6];
        for (var i = 0; i < 6; i++)
            wrench[i] = Noise(i < 3 ? 0.5 : 0.05);

        return new RobotStates(
            [.. q], [.. _dq], [.. tau], [.. tauExt], [.. _temperature], [.. FakeForwardKinematics(_q)], [.. wrench]);
    }

    /// <summary>Not real kinematics: a plausible, continuous TCP pose derived from the joints.</summary>
    private static double[] FakeForwardKinematics(double[] q)
    {
        var reach = 0.45 + 0.25 * Math.Cos(q[3] - Home[3]) + 0.1 * Math.Sin(q[1] - Home[1]);
        var x = reach * Math.Cos(q[0]);
        var y = reach * Math.Sin(q[0]);
        var z = 0.35 + 0.3 * Math.Sin(q[1] - Home[1]) + 0.1 * Math.Sin(q[5] - Home[5]);
        // Tool pointing down (180 deg about X), then rotated about Z by q0 + q6.
        var yaw = (q[0] + q[6]) / 2;
        return [x, y, z, 0, Math.Cos(yaw), Math.Sin(yaw), 0];
    }

    private double Noise(double amplitude) => (_random.NextDouble() * 2 - 1) * amplitude;

    private void AddEvent(EventLevel level, string message) =>
        _pendingEvents.Add(new RobotEvent(_time.GetUtcNow(), level, "Robot", message));
}
