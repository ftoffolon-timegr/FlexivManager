using System.Collections.Immutable;
using RobotGateway.Core;
using Rdk = FlexivRdk;

namespace RobotGateway.Flexiv;

/// <summary>Conversions between wrapper types (FlexivRdk) and gateway types (RobotGateway.Core).</summary>
internal static class FlexivMapping
{
    public static RobotMode ToCore(Rdk.RobotMode mode) => mode switch
    {
        Rdk.RobotMode.IDLE => RobotMode.Idle,
        Rdk.RobotMode.NRT_PLAN_EXECUTION => RobotMode.NrtPlanExecution,
        Rdk.RobotMode.NRT_PRIMITIVE_EXECUTION => RobotMode.NrtPrimitiveExecution,
        Rdk.RobotMode.NRT_JOINT_POSITION => RobotMode.NrtJointPosition,
        Rdk.RobotMode.NRT_CARTESIAN_MOTION_FORCE => RobotMode.NrtCartesianMotionForce,
        _ => RobotMode.Unknown, // RT modes and others are never used by the gateway
    };

    public static Rdk.RobotMode ToRdk(RobotMode mode) => mode switch
    {
        RobotMode.Idle => Rdk.RobotMode.IDLE,
        RobotMode.NrtPlanExecution => Rdk.RobotMode.NRT_PLAN_EXECUTION,
        RobotMode.NrtPrimitiveExecution => Rdk.RobotMode.NRT_PRIMITIVE_EXECUTION,
        RobotMode.NrtJointPosition => Rdk.RobotMode.NRT_JOINT_POSITION,
        RobotMode.NrtCartesianMotionForce => Rdk.RobotMode.NRT_CARTESIAN_MOTION_FORCE,
        _ => throw new InvalidOperationException($"La modalità {mode} non si può comandare."),
    };

    public static OperationalStatus ToCore(Rdk.OperationalStatus status) => status switch
    {
        Rdk.OperationalStatus.READY => OperationalStatus.Ready,
        Rdk.OperationalStatus.BOOTING => OperationalStatus.Booting,
        Rdk.OperationalStatus.ESTOP_NOT_RELEASED => OperationalStatus.EStopNotReleased,
        Rdk.OperationalStatus.NOT_ENABLED => OperationalStatus.NotEnabled,
        Rdk.OperationalStatus.RELEASING_BRAKE => OperationalStatus.ReleasingBrake,
        Rdk.OperationalStatus.MINOR_FAULT => OperationalStatus.MinorFault,
        Rdk.OperationalStatus.CRITICAL_FAULT => OperationalStatus.CriticalFault,
        Rdk.OperationalStatus.IN_REDUCED_STATE => OperationalStatus.InReducedState,
        Rdk.OperationalStatus.IN_RECOVERY_STATE => OperationalStatus.InRecoveryState,
        Rdk.OperationalStatus.IN_MANUAL_MODE => OperationalStatus.InManualMode,
        Rdk.OperationalStatus.IN_AUTO_MODE => OperationalStatus.InAutoMode,
        _ => OperationalStatus.Unknown,
    };

    /// <summary>Copies the joint-space arrays for the real DoF only (the wrapper arrays are sized for 32).</summary>
    public static RobotStates ToCore(Rdk.RobotStates s)
    {
        var dof = Math.Clamp(s.SystemDoF, 0, Rdk.FlexivConstants.kMaxSystemDoF);
        return new RobotStates(
            Slice(s.Q, dof),
            Slice(s.DQ, dof),
            Slice(s.Tau, dof),
            Slice(s.TauExt, dof),
            Slice(s.Temperature, dof),
            Slice(s.TcpPose, Rdk.FlexivConstants.kPoseSize),
            Slice(s.ExtWrenchInTcp, Rdk.FlexivConstants.kCartDoF));
    }

    public static PlanInfo ToCore(Rdk.PlanInfo p) => new(
        p.AssignedPlanName ?? "",
        p.NodeName ?? "",
        p.NodePath ?? "",
        (int)Math.Round(p.VelocityScale),
        p.WaitingForStep != 0);

    public static RobotEvent ToCore(Rdk.RobotEvent e) => new(
        e.Timestamp > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp) : DateTimeOffset.UtcNow,
        e.Level switch
        {
            Rdk.Level.ERROR or Rdk.Level.CRITICAL => EventLevel.Error,
            Rdk.Level.WARNING => EventLevel.Warning,
            _ => EventLevel.Info,
        },
        "Robot",
        string.IsNullOrWhiteSpace(e.RecommendedActions)
            ? $"[{e.Id}] {e.Description}"
            : $"[{e.Id}] {e.Description} - {e.RecommendedActions}");

    private static ImmutableArray<double> Slice(double[]? source, int count) =>
        source is null ? [] : [.. source.AsSpan(0, Math.Min(count, source.Length))];
}
