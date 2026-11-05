using RobotGateway.Core;

namespace FlexivManager.Components.Layout;

/// <summary>Label and Bootstrap badge class for each service state, shared by the top bar and pages.</summary>
public static class RobotStateStyle
{
    public static string Label(RobotServiceState state) => state switch
    {
        RobotServiceState.Disconnected => "SCOLLEGATO",
        RobotServiceState.Connected => "CONNESSO",
        RobotServiceState.Operational => "PRONTO",
        RobotServiceState.Running => "IN ESECUZIONE",
        RobotServiceState.Paused => "IN PAUSA",
        RobotServiceState.Fault => "ANOMALIA",
        RobotServiceState.EStop => "E-STOP",
        _ => state.ToString().ToUpperInvariant(),
    };

    public static string BadgeClass(RobotServiceState state) => state switch
    {
        RobotServiceState.Disconnected => "text-bg-secondary",
        RobotServiceState.Connected => "text-bg-light",
        RobotServiceState.Operational => "text-bg-success",
        RobotServiceState.Running => "text-bg-primary",
        RobotServiceState.Paused => "text-bg-warning",
        _ => "text-bg-danger",
    };
}
