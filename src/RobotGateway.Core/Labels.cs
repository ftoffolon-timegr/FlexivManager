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

    public static string Of(RemoteMode mode) => mode switch
    {
        RemoteMode.Remote => "Auto (Remote)",
        RemoteMode.NotRemote => "non in Auto (Remote)",
        _ => "non verificabile",
    };

    public static string Of(OperationalStatus status) => status switch
    {
        OperationalStatus.Ready => "pronto",
        OperationalStatus.Booting => "avvio in corso",
        OperationalStatus.EStopNotReleased => "E-stop non rilasciato",
        OperationalStatus.NotEnabled => "non abilitato",
        OperationalStatus.ReleasingBrake => "rilascio freni in corso",
        OperationalStatus.MinorFault => "anomalia lieve",
        OperationalStatus.CriticalFault => "anomalia critica",
        OperationalStatus.InReducedState => "stato ridotto",
        OperationalStatus.InRecoveryState => "stato di recupero",
        OperationalStatus.InManualMode => "in Manuale: serve Auto (Remote)",
        OperationalStatus.InAutoMode => "in Auto: serve Auto (Remote)",
        _ => "sconosciuto",
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
