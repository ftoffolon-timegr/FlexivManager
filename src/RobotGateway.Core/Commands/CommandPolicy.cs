namespace RobotGateway.Core;

/// <summary>
/// Server-side guard: which command is allowed in which service state. Evaluated on the worker thread
/// against the latest polled state, right before execution.
/// </summary>
public static class CommandPolicy
{
    /// <returns>Null when allowed, otherwise the rejection reason.</returns>
    public static string? Check(RobotServiceState state, RobotCommand command) => command switch
    {
        StopCommand => null,
        ConnectCommand => Require(state, command, RobotServiceState.Disconnected),
        DisconnectCommand => state is RobotServiceState.Running or RobotServiceState.Paused
            ? "Ferma il robot prima di disconnetterlo."
            : null,
        EnableCommand => Require(state, command, RobotServiceState.Connected),
        ClearFaultCommand => Require(state, command, RobotServiceState.Fault),
        SwitchModeCommand => Require(state, command, RobotServiceState.Operational),
        StartCycleCommand c when string.IsNullOrWhiteSpace(c.Request.Job.PlanName) => "Il nome del plan è obbligatorio.",
        StartCycleCommand => Require(state, command, RobotServiceState.Operational),
        PausePlanCommand => Require(state, command, RobotServiceState.Running),
        ResumePlanCommand => Require(state, command, RobotServiceState.Paused),
        StopPlanCommand => state is RobotServiceState.Running or RobotServiceState.Paused
            ? null
            : $"{command.Label} richiede lo stato {Labels.Of(RobotServiceState.Running)} o {Labels.Of(RobotServiceState.Paused)} (ora {Labels.Of(state)}).",
        _ => $"Comando non supportato: {command.Label}.",
    };

    private static string? Require(RobotServiceState state, RobotCommand command, RobotServiceState required) =>
        state == required ? null : $"{command.Label} richiede lo stato {Labels.Of(required)} (ora {Labels.Of(state)}).";
}
