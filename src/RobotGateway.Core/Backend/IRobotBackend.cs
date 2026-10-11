namespace RobotGateway.Core;

/// <summary>
/// Thin abstraction over the subset of FlexivRdk.Robot used by the gateway.
/// Only <see cref="RobotWorker"/> calls it, always from its own thread: implementations need not be thread-safe.
/// Members mirror the RDK (connected, operational, busy, mode, states, plan_list, plan_info,
/// Enable, ClearFault, SwitchMode, SetVelocityScale, ExecutePlan, PausePlan, Stop).
/// StopPlan and DrainEventLog have no verified RDK counterpart yet: to be checked in the RDK docs.
/// </summary>
public interface IRobotBackend : IDisposable
{
    /// <summary>Human-readable backend name, e.g. "Mock" or "Flexiv".</summary>
    string Name { get; }

    void Connect();
    void Disconnect();

    /// <summary>Never throws; false when the link is down.</summary>
    bool IsConnected { get; }

    // The members below throw RobotBackendException when not connected.
    bool IsOperational { get; }
    OperationalStatus OperationalStatus { get; }
    bool IsFault { get; }
    bool IsEStopReleased { get; }
    bool IsBusy { get; }
    RobotMode Mode { get; }

    RobotStates ReadStates();
    IReadOnlyList<string> GetPlanList();
    PlanInfo GetPlanInfo();

    /// <summary>Returns robot-side events produced since the previous call.</summary>
    IReadOnlyList<RobotEvent> DrainEventLog();

    void Enable();
    void ClearFault();
    void SwitchMode(RobotMode mode);
    void SetVelocityScale(int percent);
    void ExecutePlan(string name, bool continueExec = false);
    void PausePlan(bool pause);
    void StopPlan();

    /// <summary>Stops any motion and switches the robot to IDLE.</summary>
    void Stop();
}

/// <summary>Communication-level failure (link lost, robot not reachable, not connected).</summary>
public sealed class RobotBackendException(string message, Exception? inner = null) : Exception(message, inner);
