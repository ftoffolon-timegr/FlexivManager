# FlexivManager / RobotGateway

Gateway for a Flexiv Rizon robot. **Current step: mock robot only** – no real robot is contacted.

> The software STOP never replaces the hardware emergency stop.

## Layout

```
FlexivManager.slnx
Directory.Build.props            shared settings (x64, nullable, latest C#)
FlexivManager.csproj             Blazor host (Interactive Server)
  Program.cs                     DI wiring: options, MockBackend, SnapshotStore, EventLog, RobotCommandService, RobotWorker
  Components/Pages/RobotStatus.razor   live page /robot, UI refresh 5 Hz
src/RobotGateway.Core            no UI, no RDK
  Backend/IRobotBackend.cs       subset of FlexivRdk.Robot used by the gateway
  State/RobotStateMachine.cs     DISCONNECTED/CONNECTED/OPERATIONAL/RUNNING/PAUSED/FAULT/ESTOP (pure derivation)
  Snapshot/                      immutable RobotSnapshot + lock-free SnapshotStore
  Commands/                      command records, CommandPolicy (state guard), RobotCommandService (STOP priority, timeout)
  Worker/RobotWorker.cs          BackgroundService with dedicated thread, only caller of the backend
  Events/EventLog.cs             bounded event log
  Catalog/WorkProgram.cs         program catalog model (store comes later)
src/RobotGateway.Mock            MockBackend: simulated 7-axis robot, plans, fault/E-stop/link-loss hooks
tests/RobotGateway.Tests         xUnit: state machine, policy, command queue, mock, worker integration
```

## Run

```powershell
dotnet test FlexivManager.slnx
dotnet run --project FlexivManager.csproj --launch-profile http
# open http://localhost:5048/robot
```

Configuration: section `Robot` in `appsettings.json` (`PollRateHz`, `AutoConnect`, `ReconnectInterval`,
`DefaultCommandTimeout`, `Mock:EnableDelay`). AutoConnect only connects: it never enables or moves the robot.
