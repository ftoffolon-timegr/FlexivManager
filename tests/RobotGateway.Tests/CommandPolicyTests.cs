using RobotGateway.Core;

namespace RobotGateway.Tests;

public class CommandPolicyTests
{
    [Theory]
    [InlineData(RobotServiceState.Disconnected)]
    [InlineData(RobotServiceState.Running)]
    [InlineData(RobotServiceState.Fault)]
    [InlineData(RobotServiceState.EStop)]
    public void Stop_IsAlwaysAllowed(RobotServiceState state) =>
        Assert.Null(CommandPolicy.Check(state, new StopCommand()));

    [Theory]
    [InlineData(RobotServiceState.Disconnected)]
    [InlineData(RobotServiceState.Connected)]
    [InlineData(RobotServiceState.Running)]
    [InlineData(RobotServiceState.Paused)]
    [InlineData(RobotServiceState.Fault)]
    [InlineData(RobotServiceState.EStop)]
    public void ExecutePlan_OnlyWhenOperational(RobotServiceState state) =>
        Assert.NotNull(CommandPolicy.Check(state, new ExecutePlanCommand("P")));

    [Fact]
    public void ExecutePlan_AllowedWhenOperational() =>
        Assert.Null(CommandPolicy.Check(RobotServiceState.Operational, new ExecutePlanCommand("P")));

    [Fact]
    public void ExecutePlan_RequiresName() =>
        Assert.NotNull(CommandPolicy.Check(RobotServiceState.Operational, new ExecutePlanCommand(" ")));

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void VelocityScale_OutOfRange_IsRejected(int percent) =>
        Assert.NotNull(CommandPolicy.Check(RobotServiceState.Operational, new SetVelocityScaleCommand(percent)));

    [Fact]
    public void Disconnect_RejectedWhileRunning() =>
        Assert.NotNull(CommandPolicy.Check(RobotServiceState.Running, new DisconnectCommand()));

    [Fact]
    public void Enable_OnlyWhenConnected()
    {
        Assert.Null(CommandPolicy.Check(RobotServiceState.Connected, new EnableCommand()));
        Assert.NotNull(CommandPolicy.Check(RobotServiceState.Fault, new EnableCommand()));
    }

    [Fact]
    public void MotionCommands_AreFlagged()
    {
        Assert.True(new ExecutePlanCommand("P").IsMotion);
        Assert.True(new ResumePlanCommand().IsMotion);
        Assert.False(new StopCommand().IsMotion);
        Assert.False(new EnableCommand().IsMotion);
    }
}
