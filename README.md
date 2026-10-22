# FlexivManager / RobotGateway

Gateway for a Flexiv Rizon robot. **Current step: mock robot only** – no real robot is contacted.

> The software STOP never replaces the hardware emergency stop.

## Roles

| Host | Default address | Who | Can do |
|------|-----------------|-----|--------|
| Operator (local) | `127.0.0.1:8081` (loopback only, enforced) | operator at the cell | scan / select, Start (with confirmation), Pause, Resume, Stop, STOP, Enable, Clear fault |
| Monitoring (remote) | `Hosts:Monitoring:Address`:8080 (8090 in Development) | office / planning | monitoring, robot plan list, **jobs** (code → plan) and **work list** (orders) behind login. No command service, no backend: cannot move the robot |

Read-only API on both (GET): `/api/status`, `/api/telemetry`, `/api/events`, `/api/plans`, `/api/jobs`, `/api/orders`, `/api/cycles`.

## Planning model

- **Job**: product/job code (unique, case-insensitive) → one robot plan + velocity % (≤ `Robot:MaxVelocityScale`). Several codes may use the same plan. Versioned, with history (who/when). A save on a stale version is refused (conflict warning).
- **Order** (work list): order number (unique among open orders), code, quantity, notes. Pending → Active (first cycle) → Completed (quantity reached) or Cancelled. Only Pending orders can be edited or moved; the Active order stays on top and can only be cancelled.
- **Cycle**: one run of the job's plan. Completed cycles count for the order; Stopped/Failed cycles are recorded but not counted. Cycle history in the database.

## Local workflow

1. Scan the barcode (USB keyboard-wedge: the scan field keeps the focus) or pick an order / a one-off job.
   A scan selects the first open order with that code, otherwise the bare job (one-off). Scanning never moves the robot.
2. **Start…** → in-page confirmation (single-use token, 15 s).
3. Next cycles of the same order: **Start next cycle**, one press, as long as the previous cycle completed normally.
   After a fault, STOP or Stop the confirmation is required again.
4. The job version is read at each start: remote edits apply from the next cycle.

## Layout

```
FlexivManager.csproj             Blazor host
  Hosting/                       two-host composition, login endpoints, password hashing, demo seed
  Api/ReadOnlyApi.cs             GET-only JSON API
  Components/Pages/              Operator (local), RobotStatus, RobotPlans, Jobs/JobEdit, Orders/OrderEdit
  Components/Planning/           login state, redirect to login
  Components/Robot/StopButton    software STOP (renders nothing without the command service)
src/RobotGateway.Core            no UI, no RDK, no database
  Planning/                      Job, ProductionOrder, Cycle, validators, PlanningService (remote edits),
                                 ScanService (local), CycleRecorder (persists cycle events)
  Commands/                      RobotCommandService (STOP priority, confirmation, next-cycle arming), CommandPolicy
  Worker/RobotWorker.cs          dedicated thread, only caller of the backend, tracks the running cycle
src/RobotGateway.Data            EF Core + SQLite store (data/planning.db)
src/RobotGateway.Mock            simulated robot
tests/RobotGateway.Tests         xUnit
```

## Run

```powershell
dotnet test FlexivManager.slnx
dotnet run --project FlexivManager.csproj --launch-profile http
# local:  http://127.0.0.1:8081/operator
# remote: http://127.0.0.1:8090/orders   (login: planner / flexiv in Development only)
```

Production password: `FlexivManager.exe hash-password <password>` and put the result in
`Hosts:Monitoring:Editor:PasswordHash`. Empty hash = planning read-only. `Hosts:Monitoring:RequireLoginForEdit=false`
disables the login (not recommended).

The database schema is created at first start (`EnsureCreated`); later schema changes will need EF migrations.
