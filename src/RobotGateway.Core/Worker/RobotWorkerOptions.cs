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
}
