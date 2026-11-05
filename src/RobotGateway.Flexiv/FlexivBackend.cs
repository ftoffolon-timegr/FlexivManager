using RobotGateway.Core;
using Rdk = FlexivRdk;

namespace RobotGateway.Flexiv;

/// <summary>
/// Real robot through the official Flexiv RDK C# wrapper. Implements only the subset common to RDK 1.8 and 1.9.
/// Called exclusively by <see cref="RobotWorker"/> from its thread (the wrapper is not assumed thread-safe).
/// Never enables or moves the robot on its own: every motion comes from a worker command.
/// </summary>
/// <remarks>
/// The wrapper reports every native error as <see cref="Exception"/> with "[Flexiv RDK Error 1] ..." (the code is
/// always 1). Errors are therefore classified by checking the link afterwards: link down ->
/// <see cref="RobotBackendException"/> (the worker reconnects); link up -> <see cref="InvalidOperationException"/>
/// (command refused, e.g. wrong mode).
/// </remarks>
public sealed class FlexivBackend(FlexivBackendOptions options) : IRobotBackend
{
    private Rdk.Robot? _robot;
    private readonly EventLogCursor _events = new();

    public string Name => $"Flexiv {options.SerialNumber} (RDK {options.RdkVersion})";

    // ---- Connection --------------------------------------------------------------------------

    /// <remarks>
    /// The RDK constructor blocks until the robot is found and the connection is established (or fails):
    /// the worker does not publish snapshots meanwhile. Nothing is moving, since we are not connected.
    /// </remarks>
    public void Connect()
    {
        if (_robot is not null && SafeConnected()) return;
        Disconnect();
        if (string.IsNullOrWhiteSpace(options.SerialNumber))
            throw new RobotBackendException("Robot:Flexiv:SerialNumber non è configurato.");

        NativeRdkLoader.EnsureLoaded(options.RdkVersion);
        try
        {
            _robot = new Rdk.Robot(options.SerialNumber.Trim(), options.NetworkInterfaces, options.Verbose, lite: false);
        /*    var plans = _robot.plan_list();
            int i = 0;
            foreach (var item in plans)
            {
                i++;
            }*/
        }
        catch (Exception ex) when (ex is not RobotBackendException)
        {
            // Includes "incompatible RDK version", wrong serial, robot not reachable, RDK not enabled on the robot.
            throw new RobotBackendException($"Connessione a {options.SerialNumber} fallita: {ex.Message}", ex);
        }
        _events.Reset();
    }

    public void Disconnect()
    {
        var robot = _robot;
        _robot = null;
        try { robot?.Dispose(); }
        catch { /* best effort: the native object may already be unusable */ }
    }

    public bool IsConnected => SafeConnected();

    public void Dispose() => Disconnect();

    // ---- Status ------------------------------------------------------------------------------

    public bool IsOperational => Call(r => r.operational());
    public OperationalStatus OperationalStatus => Call(r => FlexivMapping.ToCore(r.operational_status()));
    public bool IsFault => Call(r => r.fault());
    public bool IsEStopReleased => Call(r => r.estop_released());

    /// <summary>Per robot.hpp, busy() tells whether the plan has finished: the end-of-cycle signal.</summary>
    public bool IsBusy => Call(r => r.busy());

    public RobotMode Mode => Call(r => FlexivMapping.ToCore(r.mode()));

    public RobotStates ReadStates() => Call(r => FlexivMapping.ToCore(r.states()));

    public IReadOnlyList<string> GetPlanList() => Call(r => (IReadOnlyList<string>)r.plan_list());

    public PlanInfo GetPlanInfo() => Call(r => FlexivMapping.ToCore(r.plan_info()));

    /// <summary>
    /// event_log() returns the robot log (to verify on the robot whether it is cumulative or since the last call):
    /// only entries newer than the last one returned are passed on.
    /// </summary>
    public IReadOnlyList<RobotEvent> DrainEventLog() => Call(r =>
        (IReadOnlyList<RobotEvent>)_events.TakeNew(r.event_log() ?? [], e => e.Timestamp, e => e.Id)
            .Select(FlexivMapping.ToCore)
            .ToList());

    // ---- Commands ----------------------------------------------------------------------------

    public void Enable() => Call(r => r.Enable());

    /// <summary>Blocks up to ClearFaultTimeoutSeconds; the robot is in fault, so not moving.</summary>
    public void ClearFault() => Call(r =>
    {
        if (!r.ClearFault(Math.Max(1, options.ClearFaultTimeoutSeconds)))
            throw new InvalidOperationException("Anomalia non resettata (può servire un controllo manuale sul robot).");
    });

    public void SwitchMode(RobotMode mode) => Call(r => r.SwitchMode(FlexivMapping.ToRdk(mode)));

    public void SetVelocityScale(int percent) => Call(r => r.SetVelocityScale(percent));

    /// <summary>blockUntilStarted: returns once the plan runs (typically &lt; 200 ms), so busy() is already true.</summary>
    public void ExecutePlan(string name, bool continueExec = false) =>
        Call(r => r.ExecutePlan(name, continueExec, blockUntilStarted: true));

    public void PausePlan(bool pause) => Call(r => r.PausePlan(pause));

    public void StopPlan() => Call(r => r.StopPlan());

    public void Stop() => Call(r => r.Stop());

    // ---- Helpers -----------------------------------------------------------------------------

    private bool SafeConnected()
    {
        try { return _robot?.connected() == true; }
        catch { return false; }
    }

    private T Call<T>(Func<Rdk.Robot, T> call)
    {
        var robot = _robot ?? throw new RobotBackendException("Non connesso.");
        try
        {
            return call(robot);
        }
        catch (Exception ex) when (ex is not (RobotBackendException or InvalidOperationException))
        {
            throw SafeConnected()
                ? new InvalidOperationException(ex.Message, ex)
                : new RobotBackendException($"Collegamento perso: {ex.Message}", ex);
        }
    }

    private void Call(Action<Rdk.Robot> call) => Call(r => { call(r); return 0; });
}
