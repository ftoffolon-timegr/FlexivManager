namespace RobotGateway.Mock;

/// <summary>A simulated robot-side plan.</summary>
/// <param name="Name">Plan name as it would appear in Flexiv Elements.</param>
/// <param name="Duration">Execution time at 100 % velocity scale.</param>
/// <param name="Nodes">Node names cycled through while running (shown as PlanInfo.NodeName).</param>
/// <param name="FaultAfter">If set, the plan raises a fault after this execution time.</param>
public sealed record MockPlan(string Name, TimeSpan Duration, string[] Nodes, TimeSpan? FaultAfter = null);

/// <summary>Bound from "Robot:Mock".</summary>
public sealed class MockBackendOptions
{
    public const string SectionName = "Robot:Mock";

    /// <summary>Time spent in RELEASING_BRAKE after Enable before becoming operational.</summary>
    public TimeSpan EnableDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Seed for the measurement noise, for reproducible tests.</summary>
    public int Seed { get; set; } = 42;

    public List<MockPlan> Plans { get; set; } =
    [
        new("PLAN-PickPlace", TimeSpan.FromSeconds(8), ["Start", "MoveJ_Approach", "MoveL_Pick", "Grip", "MoveL_Place", "Release", "MoveJ_Home"]),
        new("PLAN-Polish", TimeSpan.FromSeconds(12), ["Start", "MoveJ_Approach", "Contact", "PolishPattern", "Retract"]),
        new("PLAN-Home", TimeSpan.FromSeconds(4), ["Start", "MoveJ_Home"]),
        new("PLAN-FaultDemo", TimeSpan.FromSeconds(10), ["Start", "MoveL_Collision"], FaultAfter: TimeSpan.FromSeconds(3)),
    ];
}
