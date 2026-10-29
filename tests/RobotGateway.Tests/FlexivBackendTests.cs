using RobotGateway.Core;
using RobotGateway.Flexiv;
using Rdk = FlexivRdk;

namespace RobotGateway.Tests;

/// <summary>Flexiv backend without the robot and without the native DLL: mapping and failure paths only.</summary>
public class FlexivBackendTests
{
    [Theory]
    [InlineData(Rdk.RobotMode.IDLE, RobotMode.Idle)]
    [InlineData(Rdk.RobotMode.NRT_PLAN_EXECUTION, RobotMode.NrtPlanExecution)]
    [InlineData(Rdk.RobotMode.NRT_PRIMITIVE_EXECUTION, RobotMode.NrtPrimitiveExecution)]
    [InlineData(Rdk.RobotMode.RT_JOINT_TORQUE, RobotMode.Unknown)]
    public void Mode_IsMapped(Rdk.RobotMode rdk, RobotMode core) =>
        Assert.Equal(core, FlexivMapping.ToCore(rdk));

    [Fact]
    public void CommandedModes_RoundTrip()
    {
        foreach (var mode in new[] { RobotMode.Idle, RobotMode.NrtPlanExecution, RobotMode.NrtPrimitiveExecution })
            Assert.Equal(mode, FlexivMapping.ToCore(FlexivMapping.ToRdk(mode)));
        Assert.Throws<InvalidOperationException>(() => FlexivMapping.ToRdk(RobotMode.Unknown));
    }

    [Fact]
    public void EveryOperationalStatus_IsMapped()
    {
        foreach (var status in Enum.GetValues<Rdk.OperationalStatus>().Where(s => s != Rdk.OperationalStatus.UNKNOWN))
            Assert.NotEqual(OperationalStatus.Unknown, FlexivMapping.ToCore(status));
        Assert.Equal(OperationalStatus.CriticalFault, FlexivMapping.ToCore(Rdk.OperationalStatus.CRITICAL_FAULT));
    }

    [Fact]
    public void States_AreCutToTheRealDegreesOfFreedom()
    {
        double[] Full(double start) => Enumerable.Range(0, Rdk.FlexivConstants.kMaxSystemDoF).Select(i => start + i).ToArray();
        var rdk = new Rdk.RobotStates
        {
            SystemDoF = 7,
            Q = Full(0), DQ = Full(100), Tau = Full(200), TauExt = Full(300), Temperature = Full(400),
            TcpPose = [0.5, 0.1, 0.3, 1, 0, 0, 0],
            ExtWrenchInTcp = [1, 2, 3, 4, 5, 6],
        };

        var states = FlexivMapping.ToCore(rdk);

        Assert.Equal(7, states.Q.Length);
        Assert.Equal(6, states.Q[6]);
        Assert.Equal(406, states.Temperature[6]);
        Assert.Equal(7, states.TcpPose.Length);
        Assert.Equal(6, states.ExtWrenchInTcp.Length);
    }

    [Fact]
    public void PlanInfoAndEvents_AreMapped()
    {
        var plan = FlexivMapping.ToCore(new Rdk.PlanInfo { AssignedPlanName = "PLAN-A", NodeName = "MoveL", NodePath = "root/MoveL", VelocityScale = 49.6, WaitingForStep = 1 });
        Assert.Equal(("PLAN-A", "MoveL", 50, true), (plan.AssignedPlanName, plan.NodeName, plan.VelocityScale, plan.WaitingForStep));

        var e = FlexivMapping.ToCore(new Rdk.RobotEvent { Level = Rdk.Level.CRITICAL, Id = 42, Description = "Collision", RecommendedActions = "Clear fault", Timestamp = 1_700_000_000_000 });
        Assert.Equal(EventLevel.Error, e.Level);
        Assert.Contains("Collision", e.Message);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), e.Timestamp);
    }

    [Fact]
    public void EventLogCursor_PassesOnOnlyNewEntries()
    {
        var cursor = new EventLogCursor();
        (long Ts, int Id)[] first = [(10, 1), (20, 2), (20, 3)];
        (long Ts, int Id)[] second = [(10, 1), (20, 2), (20, 3), (20, 4), (30, 5)]; // cumulative log

        Assert.Equal(3, cursor.TakeNew(first, e => e.Ts, e => e.Id).Count);
        Assert.Equal([4, 5], cursor.TakeNew(second, e => e.Ts, e => e.Id).Select(e => e.Id));
        Assert.Empty(cursor.TakeNew(second, e => e.Ts, e => e.Id));
    }

    [Fact]
    public void NotConnected_ReportsDisconnected_AndThrowsBackendException()
    {
        using var backend = new FlexivBackend(new FlexivBackendOptions { SerialNumber = "Rizon4-062609" });

        Assert.False(backend.IsConnected);
        Assert.Throws<RobotBackendException>(() => backend.ReadStates());
        Assert.Throws<RobotBackendException>(() => backend.Stop());
    }

    [Fact]
    public void Connect_WithoutSerialNumber_Fails()
    {
        using var backend = new FlexivBackend(new FlexivBackendOptions());
        Assert.Contains("SerialNumber", Assert.Throws<RobotBackendException>(backend.Connect).Message);
    }

    [Fact]
    public void NativeDll_ForRdk18_LoadsAndExportsTheUsedFunctions()
    {
        var dll = Path.Combine(NativeRdkLoader.FolderFor("1.8"), NativeRdkLoader.DllName);
        if (!File.Exists(dll))
            return; // not built on this machine (third_party/build-native.ps1)

        NativeRdkLoader.EnsureLoaded("1.8"); // load only: no robot object, no network

        var handle = System.Runtime.InteropServices.NativeLibrary.Load(dll);
        foreach (var export in new[] { "CreateFlexivRobot", "IsConnected", "IsOperational", "IsBusy", "GetStates",
                     "GetPlanList", "GetPlanInfo", "ExecutePlanByName", "PausePlan", "StopPlan", "Stop", "Enable",
                     "ClearFault", "SwitchMode", "SetVelocityScale", "GetEventLog" })
            Assert.True(System.Runtime.InteropServices.NativeLibrary.TryGetExport(handle, export, out _), export);
    }

    [Fact]
    public void Connect_WithoutNativeDll_FailsWithBuildHint()
    {
        using var backend = new FlexivBackend(new FlexivBackendOptions { SerialNumber = "Rizon4-062609", RdkVersion = "0.0" });
        Assert.Contains("build-native.ps1", Assert.Throws<RobotBackendException>(backend.Connect).Message);
    }
}
