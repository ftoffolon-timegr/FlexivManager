using System.ComponentModel.DataAnnotations;

namespace RobotGateway.Core;

/// <summary>Bound from the "Robot" configuration section.</summary>
public sealed class RobotWorkerOptions
{
    public const string SectionName = "Robot";

    /// <summary>Backend polling and snapshot publishing rate.</summary>
    [Range(1, 200)]
    public double PollRateHz { get; set; } = 50;

    /// <summary>Connect (and reconnect after a link loss) automatically. Never enables or moves the robot.</summary>
    public bool AutoConnect { get; set; } = true;

    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(3);

    public TimeSpan DefaultCommandTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Validity of a motion confirmation token (request -> confirm).</summary>
    public TimeSpan ConfirmationTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Global velocity limit [%] for every job.</summary>
    [Range(1, 100)]
    public int MaxVelocityScale { get; set; } = 100;

    /// <summary>A started plan must be seen running within this time, otherwise the cycle fails.</summary>
    public TimeSpan PlanStartTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the worker re-reads the robot plan list while idle.</summary>
    public TimeSpan PlanListRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);
}
