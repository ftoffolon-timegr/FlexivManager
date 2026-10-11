using System.Collections.Immutable;

namespace RobotGateway.Core;

/// <summary>
/// Immutable copy of the RDK RobotStates fields the gateway publishes.
/// Units follow the RDK: rad, rad/s, Nm, degC, m + quaternion, N/Nm.
/// </summary>
/// <param name="Q">Joint positions [rad], one per joint (7 on Rizon).</param>
/// <param name="Dq">Joint velocities [rad/s].</param>
/// <param name="Tau">Measured joint torques [Nm].</param>
/// <param name="TauExt">Estimated external joint torques [Nm].</param>
/// <param name="Temperature">Joint temperatures [degC].</param>
/// <param name="TcpPose">TCP pose in world frame: x, y, z [m], qw, qx, qy, qz.</param>
/// <param name="ExtWrenchInTcp">External wrench at TCP, TCP frame: fx, fy, fz [N], mx, my, mz [Nm].</param>
public sealed record RobotStates(
    ImmutableArray<double> Q,
    ImmutableArray<double> Dq,
    ImmutableArray<double> Tau,
    ImmutableArray<double> TauExt,
    ImmutableArray<double> Temperature,
    ImmutableArray<double> TcpPose,
    ImmutableArray<double> ExtWrenchInTcp);

/// <summary>
/// Plan execution info (subset of RDK PlanInfo). Field names to verify against the RDK version in use.
/// </summary>
public sealed record PlanInfo(
    string AssignedPlanName,
    string NodeName,
    string NodePath,
    int VelocityScale,
    bool WaitingForStep)
{
    public static PlanInfo Empty { get; } = new("", "", "", 0, false);
}
