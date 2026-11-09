namespace RobotGateway.Core;

/// <summary>Raw flags polled from the backend in one worker cycle.</summary>
public readonly record struct BackendStatus(
    bool Connected,
    bool Operational,
    OperationalStatus OperationalStatus,
    bool Fault,
    bool EStopReleased,
    bool Busy,
    RobotMode Mode)
{
    public static BackendStatus Disconnected => default;

    /// <summary>
    /// Whether the robot is in Auto (Remote), the only mode in which it accepts RDK commands.
    /// The RDK has no direct flag: OperationalStatus reports IN_MANUAL_MODE / IN_AUTO_MODE ("need to switch to
    /// Auto (Remote)"), while READY implies Auto (Remote). Other statuses (not enabled, fault, ...) hide the mode.
    /// </summary>
    public RemoteMode RemoteMode =>
        !Connected ? RemoteMode.Unknown
        : OperationalStatus is OperationalStatus.InManualMode or OperationalStatus.InAutoMode ? RemoteMode.NotRemote
        : Operational || OperationalStatus == OperationalStatus.Ready ? RemoteMode.Remote
        : RemoteMode.Unknown;
}

public enum RemoteMode
{
    /// <summary>Not connected, or the robot reports another cause first (not enabled, fault, E-stop...).</summary>
    Unknown,

    /// <summary>Auto (Remote): commands from this service are accepted.</summary>
    Remote,

    /// <summary>Manual or regular Auto: switch to Auto (Remote) on the robot.</summary>
    NotRemote,
}

/// <summary>
/// Pure derivation of the service state from backend flags. Priority: link, E-stop, fault, enable, motion.
/// </summary>
public static class RobotStateMachine
{
    /// <param name="status">Flags polled from the backend.</param>
    /// <param name="pauseLatched">
    /// True after a successful PausePlan(true). The RDK exposes no explicit "paused" flag
    /// (to be verified), so the worker latches it.
    /// </param>
    public static RobotServiceState Derive(BackendStatus status, bool pauseLatched)
    {
        if (!status.Connected) return RobotServiceState.Disconnected;
        if (!status.EStopReleased) return RobotServiceState.EStop;
        if (status.Fault) return RobotServiceState.Fault;
        if (!status.Operational) return RobotServiceState.Connected;
        if (pauseLatched && status.Mode == RobotMode.NrtPlanExecution) return RobotServiceState.Paused;
        if (status.Busy) return RobotServiceState.Running;
        return RobotServiceState.Operational;
    }

    /// <summary>Whether a pause latch can still be meaningful in the given status.</summary>
    public static bool CanStayPaused(BackendStatus status) =>
        status is { Connected: true, EStopReleased: true, Fault: false, Operational: true, Mode: RobotMode.NrtPlanExecution };
}
