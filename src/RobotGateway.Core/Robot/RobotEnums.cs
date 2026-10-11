namespace RobotGateway.Core;

/// <summary>
/// Gateway-level state machine. Derived from the polled backend status, never set directly.
/// </summary>
public enum RobotServiceState
{
    Disconnected,
    Connected,
    Operational,
    Running,
    Paused,
    Fault,
    EStop,
}

/// <summary>
/// Subset of the RDK control modes the gateway uses.
/// Names mirror flexiv::rdk::Mode (IDLE, NRT_PLAN_EXECUTION, ...); verify against the RDK version in use.
/// </summary>
public enum RobotMode
{
    Unknown,
    Idle,
    NrtPlanExecution,
    NrtPrimitiveExecution,
    NrtJointPosition,
    NrtCartesianMotionForce,
}

/// <summary>
/// Subset of flexiv::rdk::OperationalStatus. Verify the full list against the RDK version in use.
/// </summary>
public enum OperationalStatus
{
    Unknown,
    Ready,
    Booting,
    EStopNotReleased,
    NotEnabled,
    ReleasingBrake,
    MinorFault,
    InReducedState,
    InRecoveryState,
}
