using RobotGateway.Core;
using RobotGateway.Mock;

namespace RobotGateway.Tests;

public class MockBackendTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly MockBackend _robot;

    public MockBackendTests()
    {
        _robot = new MockBackend(new MockBackendOptions(), _time);
    }

    private void ConnectAndEnable()
    {
        _robot.Connect();
        _robot.Enable();
        _time.Advance(2);
        Assert.True(_robot.IsOperational);
    }

    [Fact]
    public void NotConnected_Throws()
    {
        Assert.False(_robot.IsConnected);
        Assert.Throws<RobotBackendException>(() => _robot.IsOperational);
        Assert.Throws<RobotBackendException>(() => _robot.ReadStates());
    }

    [Fact]
    public void Connect_IsNotEnabled_AndIdle()
    {
        _robot.Connect();

        Assert.True(_robot.IsConnected);
        Assert.False(_robot.IsOperational);
        Assert.Equal(OperationalStatus.NotEnabled, _robot.OperationalStatus);
        Assert.Equal(RobotMode.Idle, _robot.Mode);
        Assert.False(_robot.IsBusy);
    }

    [Fact]
    public void Enable_BecomesOperationalAfterDelay()
    {
        _robot.Connect();
        _robot.Enable();
        Assert.Equal(OperationalStatus.ReleasingBrake, _robot.OperationalStatus);

        _time.Advance(1.0);
        Assert.False(_robot.IsOperational);

        _time.Advance(1.0);
        Assert.True(_robot.IsOperational);
        Assert.Equal(OperationalStatus.Ready, _robot.OperationalStatus);
    }

    [Fact]
    public void ExecutePlan_RequiresPlanMode()
    {
        ConnectAndEnable();
        Assert.Throws<InvalidOperationException>(() => _robot.ExecutePlan("PLAN-Home"));
        Assert.False(_robot.IsBusy);
    }

    [Fact]
    public void Plan_RunsMovesAndFinishes()
    {
        ConnectAndEnable();
        _robot.SwitchMode(RobotMode.NrtPlanExecution);
        var q0 = _robot.ReadStates().Q;

        _robot.ExecutePlan("PLAN-Home"); // 4 s
        _time.Advance(1);
        var running = _robot.ReadStates();
        Assert.True(_robot.IsBusy);
        Assert.Equal("PLAN-Home", _robot.GetPlanInfo().AssignedPlanName);
        Assert.True(Math.Abs(running.Q[0] - q0[0]) > 0.01);

        _time.Advance(3.1);
        Assert.False(_robot.IsBusy);
        Assert.Equal(7, _robot.ReadStates().Q.Length);
    }

    [Fact]
    public void VelocityScale_SlowsThePlan()
    {
        ConnectAndEnable();
        _robot.SwitchMode(RobotMode.NrtPlanExecution);
        _robot.SetVelocityScale(50);
        _robot.ExecutePlan("PLAN-Home"); // 4 s at 100 %

        _time.Advance(5);
        Assert.True(_robot.IsBusy);
        _time.Advance(3.5);
        Assert.False(_robot.IsBusy);
    }

    [Fact]
    public void Pause_FreezesMotion()
    {
        ConnectAndEnable();
        _robot.SwitchMode(RobotMode.NrtPlanExecution);
        _robot.ExecutePlan("PLAN-PickPlace");
        _time.Advance(1);
        _robot.PausePlan(true);
        var paused = _robot.ReadStates().Q;

        _time.Advance(20);
        var later = _robot.ReadStates().Q;

        Assert.True(_robot.IsBusy);
        for (var i = 0; i < paused.Length; i++)
            Assert.Equal(paused[i], later[i], 3);
    }

    [Fact]
    public void Stop_EndsMotion_AndSwitchesToIdle()
    {
        ConnectAndEnable();
        _robot.SwitchMode(RobotMode.NrtPlanExecution);
        _robot.ExecutePlan("PLAN-Polish");
        _time.Advance(1);

        _robot.Stop();

        Assert.False(_robot.IsBusy);
        Assert.Equal(RobotMode.Idle, _robot.Mode);
    }

    [Fact]
    public void FaultPlan_RaisesFault_ClearFaultRequiresReEnable()
    {
        ConnectAndEnable();
        _robot.SwitchMode(RobotMode.NrtPlanExecution);
        _robot.ExecutePlan("PLAN-FaultDemo");
        _time.Advance(3.5);

        Assert.True(_robot.IsFault);
        Assert.False(_robot.IsBusy);
        Assert.False(_robot.IsOperational);
        Assert.Contains(_robot.DrainEventLog(), e => e.Level == EventLevel.Error);

        _robot.ClearFault();
        Assert.False(_robot.IsFault);
        Assert.False(_robot.IsOperational);
    }

    [Fact]
    public void EStop_StopsAndDisables()
    {
        ConnectAndEnable();
        _robot.SwitchMode(RobotMode.NrtPlanExecution);
        _robot.ExecutePlan("PLAN-Polish");

        _robot.SetEStop(true);

        Assert.False(_robot.IsEStopReleased);
        Assert.False(_robot.IsBusy);
        Assert.False(_robot.IsOperational);
        Assert.Throws<InvalidOperationException>(() => _robot.Enable());
    }

    [Fact]
    public void LinkLoss_ThrowsAndBlocksConnect()
    {
        _robot.Connect();
        _robot.SimulateLinkLoss(true);

        Assert.False(_robot.IsConnected);
        Assert.Throws<RobotBackendException>(() => _robot.ReadStates());
        Assert.Throws<RobotBackendException>(() => _robot.Connect());

        _robot.SimulateLinkLoss(false);
        _robot.Connect();
        Assert.True(_robot.IsConnected);
    }
}
