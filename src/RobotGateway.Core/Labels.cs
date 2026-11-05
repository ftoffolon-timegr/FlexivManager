namespace RobotGateway.Core;

/// <summary>
/// Italian labels of the enum values that appear in messages (command results, event log, errors).
/// The enums themselves stay in English: they are code and API values.
/// </summary>
public static class Labels
{
    public static string Of(RobotServiceState state) => state switch
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

    public static string Of(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Succeeded => "eseguito",
        CommandOutcome.Rejected => "rifiutato",
        CommandOutcome.Failed => "fallito",
        CommandOutcome.TimedOut => "tempo scaduto",
        CommandOutcome.Cancelled => "annullato",
        _ => outcome.ToString(),
    };

    public static string Of(CycleOutcome outcome) => outcome switch
    {
        CycleOutcome.Completed => "completato",
        CycleOutcome.Stopped => "fermato",
        CycleOutcome.Failed => "fallito",
        _ => outcome.ToString(),
    };

    public static string Of(OrderStatus status) => status switch
    {
        OrderStatus.Pending => "da iniziare",
        OrderStatus.Active => "in corso",
        OrderStatus.Completed => "completato",
        OrderStatus.Cancelled => "annullato",
        OrderStatus.CarriedOver => "riportato",
        _ => status.ToString(),
    };
}
