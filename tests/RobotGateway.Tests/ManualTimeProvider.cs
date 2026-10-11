namespace RobotGateway.Tests;

/// <summary>Deterministic clock for stepping the mock simulation.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;
    public override long GetTimestamp() => _now.UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan delta) => _now += delta;
    public void Advance(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}
