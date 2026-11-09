using RobotGateway.Core;

namespace RobotGateway.Tests;

public class RobotStateMachineTests
{
    private static readonly BackendStatus Ready = new(
        Connected: true, Operational: true, OperationalStatus.Ready, Fault: false, EStopReleased: true, Busy: false, RobotMode.Idle);

    [Theory]
    [InlineData(true, OperationalStatus.Ready, RemoteMode.Remote)]
    [InlineData(false, OperationalStatus.InManualMode, RemoteMode.NotRemote)]
    [InlineData(false, OperationalStatus.InAutoMode, RemoteMode.NotRemote)]
    [InlineData(false, OperationalStatus.NotEnabled, RemoteMode.Unknown)]
    [InlineData(false, OperationalStatus.MinorFault, RemoteMode.Unknown)]
    public void RemoteMode_FollowsTheOperationalStatus(bool operational, OperationalStatus status, RemoteMode expected) =>
        Assert.Equal(expected, (Ready with { Operational = operational, OperationalStatus = status }).RemoteMode);

    [Fact]
    public void RemoteMode_IsUnknown_WhenNotConnected() =>
        Assert.Equal(RemoteMode.Unknown, BackendStatus.Disconnected.RemoteMode);

    [Fact]
    public void Disconnected_WhenNotConnected() =>
        Assert.Equal(RobotServiceState.Disconnected, RobotStateMachine.Derive(BackendStatus.Disconnected, false));

    [Fact]
    public void Connected_WhenNotOperational() =>
        Assert.Equal(RobotServiceState.Connected, RobotStateMachine.Derive(Ready with { Operational = false }, false));

    [Fact]
    public void Operational_WhenReadyAndIdle() =>
        Assert.Equal(RobotServiceState.Operational, RobotStateMachine.Derive(Ready, false));

    [Fact]
    public void Running_WhenBusy() =>
        Assert.Equal(RobotServiceState.Running, RobotStateMachine.Derive(Ready with { Busy = true, Mode = RobotMode.NrtPlanExecution }, false));

    [Fact]
    public void Paused_WhenLatchedInPlanMode() =>
        Assert.Equal(RobotServiceState.Paused, RobotStateMachine.Derive(Ready with { Busy = true, Mode = RobotMode.NrtPlanExecution }, true));

    [Fact]
    public void EStop_HasPriorityOverFault() =>
        Assert.Equal(RobotServiceState.EStop, RobotStateMachine.Derive(Ready with { EStopReleased = false, Fault = true, Busy = true }, true));

    [Fact]
    public void Fault_HasPriorityOverMotion() =>
        Assert.Equal(RobotServiceState.Fault, RobotStateMachine.Derive(Ready with { Fault = true, Busy = true }, false));

    [Fact]
    public void PauseLatch_IsDroppedOutsidePlanMode()
    {
        Assert.False(RobotStateMachine.CanStayPaused(Ready with { Mode = RobotMode.Idle }));
        Assert.False(RobotStateMachine.CanStayPaused(Ready with { Mode = RobotMode.NrtPlanExecution, Fault = true }));
        Assert.True(RobotStateMachine.CanStayPaused(Ready with { Mode = RobotMode.NrtPlanExecution, Busy = true }));
    }
}
