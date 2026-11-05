using RobotGateway.Core;

namespace FlexivManager.Components.Layout;

/// <summary>Italian labels for enum values shown in the UI. Keep the enums themselves in English (code).</summary>
public static class UiLabels
{
    public static string OrderStatusLabel(OrderStatus status) => status switch
    {
        OrderStatus.Pending => "da iniziare",
        OrderStatus.Active => "in corso",
        OrderStatus.Completed => "completato",
        OrderStatus.Cancelled => "annullato",
        OrderStatus.CarriedOver => "riportato",
        _ => status.ToString(),
    };

    public static string CycleOutcomeLabel(CycleOutcome outcome) => outcome switch
    {
        CycleOutcome.Completed => "completato",
        CycleOutcome.Stopped => "fermato",
        CycleOutcome.Failed => "fallito",
        _ => outcome.ToString(),
    };
}
