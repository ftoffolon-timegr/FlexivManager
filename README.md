# FlexivManager / RobotGateway

Gateway for a Flexiv Rizon robot. **Current step: mock robot only** – no real robot is contacted.

> The software STOP never replaces the hardware emergency stop.

## Hosts (one process)

| Host | Default address | Content |
|------|-----------------|---------|
| Operator | `127.0.0.1:8081` (loopback only, enforced) | all pages incl. `/operator` with commands, STOP, `/api/*` |
| Monitoring | `Hosts:Monitoring:Address`:8080 (8090 in Development) | read-only pages, `/api/*`; **no command service, no backend** |

Read-only API (GET): `/api/status`, `/api/telemetry`, `/api/events?max=50`, `/api/programs`.

If the monitoring host cannot start (e.g. port in use) the error is logged and the operator host keeps running.

## Layout

```
FlexivManager.csproj             Blazor host (Interactive Server)
  Program.cs                     -> Hosting/GatewayComposition.BuildOperatorApp
  Hosting/                       two-host composition, MonitoringHostService, HostRole, endpoint options
  Api/ReadOnlyApi.cs             GET-only JSON API + DTOs
  Components/Pages/Operator.razor   lifecycle, program list (catalog ∩ robot), confirmation dialog, execution view
  Components/Pages/RobotStatus.razor live diagnostics
  Components/Robot/StopButton.razor  software STOP, renders nothing without the command service
  programs.json                  program catalog (schemaVersion 1)
src/RobotGateway.Core            no UI, no RDK
  Backend/IRobotBackend.cs       subset of FlexivRdk.Robot used by the gateway
  State/RobotStateMachine.cs     DISCONNECTED/CONNECTED/OPERATIONAL/RUNNING/PAUSED/FAULT/ESTOP
  Snapshot/                      immutable RobotSnapshot + lock-free SnapshotStore
  Commands/                      commands, CommandPolicy, RobotCommandService (STOP priority, 2-step motion confirmation)
  Catalog/                       WorkProgram, ProgramCatalog (validated JSON)
  Worker/RobotWorker.cs          dedicated thread, only caller of the backend
src/RobotGateway.Mock            simulated 7-axis robot
tests/RobotGateway.Tests         xUnit (policy, confirmation, queue, mock, worker, hosting)
```

## Commands

- Non-motion: `RobotCommandService.EnableAsync/ClearFaultAsync/PausePlanAsync/StopPlanAsync/ConnectAsync(source)`.
- Motion (`RunPlan`, `Resume`): `RequestRunProgram(id, velocity, source)` / `RequestResume(source)` returns a
  single-use token valid `Robot:ConfirmationTimeout` (15 s); then `ConfirmAsync(token, source)`.
  Only catalog programs, velocity ≤ `maxVelocityScale`, state checked at request and again before execution.
- STOP: `RobotCommandService.StopAsync(source)` – always accepted, jumps the queue, cancels queued commands,
  voids pending confirmations. In UI use `<StopButton Source="..." />` (operator host only).

## Run

```powershell
dotnet test FlexivManager.slnx
dotnet run --project FlexivManager.csproj --launch-profile http
# operator:   http://127.0.0.1:8081/operator
# monitoring: http://127.0.0.1:8090/robot   (8080 in production config)
```
