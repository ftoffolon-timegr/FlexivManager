namespace RobotGateway.Core;

/// <summary>Base type of every command sent to the worker.</summary>
public abstract record RobotCommand
{
    /// <summary>
    /// True when executing the command can make the robot move. Motion commands are accepted only
    /// through the two-step confirmation of <see cref="RobotCommandService"/>.
    /// </summary>
    public virtual bool IsMotion => false;

    /// <summary>Technical name (type name without "Command").</summary>
    public virtual string Name => GetType().Name.Replace("Command", "");

    /// <summary>Italian name shown to users (messages, event log).</summary>
    public string Label => this switch
    {
        StopCommand => "STOP",
        ConnectCommand => "Connessione",
        DisconnectCommand => "Disconnessione",
        EnableCommand => "Abilitazione",
        ClearFaultCommand => "Reset anomalia",
        SwitchModeCommand => "Cambio modalità",
        StartCycleCommand => "Avvio ciclo",
        PausePlanCommand => "Pausa",
        ResumePlanCommand => "Ripresa",
        StopPlanCommand => "Stop plan",
        _ => Name,
    };

    /// <summary>Human-readable description for logs and confirmation dialogs.</summary>
    public virtual string Describe() => Label;
}

/// <summary>Highest priority: always accepted, flushes queued commands, stops motion and goes to IDLE.</summary>
public sealed record StopCommand : RobotCommand;

public sealed record ConnectCommand : RobotCommand;

public sealed record DisconnectCommand : RobotCommand;

public sealed record EnableCommand : RobotCommand;

public sealed record ClearFaultCommand : RobotCommand;

public sealed record SwitchModeCommand(RobotMode Mode) : RobotCommand;

/// <summary>Runs one cycle: the job's robot plan once, counted for the order if any.</summary>
public sealed record StartCycleCommand(CycleRequest Request) : RobotCommand
{
    public override bool IsMotion => true;

    public override string Describe() =>
        $"Esegui il plan '{Request.Job.PlanName}' per il codice '{Request.Job.ProductCode}'"
        + (Request.OrderId is null ? " (servizio, non conteggiato)" : Request.OrderNumber is { Length: > 0 } number ? $", ordine {number}" : ", ordine di oggi");
}

public sealed record PausePlanCommand : RobotCommand;

public sealed record ResumePlanCommand : RobotCommand
{
    public override bool IsMotion => true;

    public override string Describe() => "Riprendi il plan in pausa";
}

/// <summary>Stops the running plan; the cycle ends as Stopped and is not counted.</summary>
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
