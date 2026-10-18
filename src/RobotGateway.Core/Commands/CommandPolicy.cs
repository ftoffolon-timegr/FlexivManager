namespace RobotGateway.Core;

/// <summary>
/// Server-side guard: which command is allowed in which service state. Evaluated on the worker thread
/// against the latest polled state, right before execution.
/// </summary>
public static class CommandPolicy
{
    public const int MinVelocityScale = 1;
    public const int MaxVelocityScale = 100;

    /// <returns>Null when allowed, otherwise the rejection reason.</returns>
    public static string? Check(RobotServiceState state, RobotCommand command) => command switch
    {
        StopCommand => null,
        ConnectCommand => Require(state, command, RobotServiceState.Disconnected),
        DisconnectCommand => state is RobotServiceState.Running or RobotServiceState.Paused
            ? "Stop the robot before disconnecting."
            : null,
        EnableCommand => Require(state, command, RobotServiceState.Connected),
        ClearFaultCommand => Require(state, command, RobotServiceState.Fault),
        SwitchModeCommand => Require(state, command, RobotServiceState.Operational),
        SetVelocityScaleCommand c when c.Percent is < MinVelocityScale or > MaxVelocityScale =>
            $"Velocity scale must be within {MinVelocityScale}..{MaxVelocityScale} %.",
        SetVelocityScaleCommand => state is RobotServiceState.Disconnected or RobotServiceState.EStop
            ? $"SetVelocityScale not allowed in {state}."
            : null,
        RunPlanCommand c when string.IsNullOrWhiteSpace(c.PlanName) => "Plan name is required.",
        RunPlanCommand c when c.VelocityScale is < MinVelocityScale or > MaxVelocityScale =>
            $"Velocity scale must be within {MinVelocityScale}..{MaxVelocityScale} %.",
        RunPlanCommand => Require(state, command, RobotServiceState.Operational),
        PausePlanCommand => Require(state, command, RobotServiceState.Running),
        ResumePlanCommand => Require(state, command, RobotServiceState.Paused),
        StopPlanCommand => state is RobotServiceState.Running or RobotServiceState.Paused
            ? null
            : $"StopPlan requires RUNNING or PAUSED (current {state}).",
        _ => $"Unsupported command {command.Name}.",
    };

    private static string? Require(RobotServiceState state, RobotCommand command, RobotServiceState required) =>
        state == required ? null : $"{command.Name} requires {required} (current {state}).";
}
