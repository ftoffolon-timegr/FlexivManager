namespace RobotGateway.Core;

/// <summary>Base type of every command sent to the worker.</summary>
public abstract record RobotCommand
{
    /// <summary>True when executing the command can make the robot move.</summary>
    public virtual bool IsMotion => false;

    public virtual string Name => GetType().Name.Replace("Command", "");
}

/// <summary>Highest priority: always accepted, flushes queued commands, stops motion and goes to IDLE.</summary>
public sealed record StopCommand : RobotCommand;

public sealed record ConnectCommand : RobotCommand;

public sealed record DisconnectCommand : RobotCommand;

public sealed record EnableCommand : RobotCommand;

public sealed record ClearFaultCommand : RobotCommand;

public sealed record SwitchModeCommand(RobotMode Mode) : RobotCommand;

public sealed record SetVelocityScaleCommand(int Percent) : RobotCommand;

public sealed record ExecutePlanCommand(string PlanName) : RobotCommand
{
    public override bool IsMotion => true;
}

public sealed record PausePlanCommand : RobotCommand;

public sealed record ResumePlanCommand : RobotCommand
{
    public override bool IsMotion => true;
}

public sealed record StopPlanCommand : RobotCommand;

public enum CommandOutcome
{
    Succeeded,
    Rejected,
    Failed,
    TimedOut,
    Cancelled,
}

public sealed record CommandResult(CommandOutcome Outcome, string? Message = null)
{
    public bool IsSuccess => Outcome == CommandOutcome.Succeeded;

    public static CommandResult Ok(string? message = null) => new(CommandOutcome.Succeeded, message);
    public static CommandResult Rejected(string message) => new(CommandOutcome.Rejected, message);
    public static CommandResult Failed(string message) => new(CommandOutcome.Failed, message);
    public static CommandResult TimedOut(string message) => new(CommandOutcome.TimedOut, message);
    public static CommandResult Cancelled(string message) => new(CommandOutcome.Cancelled, message);
}
