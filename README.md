# FlexivManager / RobotGateway

Gateway for a Flexiv Rizon robot. **Current step: mock robot only** – no real robot is contacted.

> The software STOP never replaces the hardware emergency stop.

## Roles

| Host | Default address | Who | Can do |
|------|-----------------|-----|--------|
| Operator (local) | `127.0.0.1:8081` (loopback only, enforced) | operator at the cell | scan / select a row of today's plan or a service code, START (confirmation on the first piece of a row), STOP, Enable, Clear fault |
| Monitoring (remote) | `Hosts:Monitoring:Address`:8080 (8090 in Development) | office / planning | monitoring, robot plan list, **jobs** (code → plan) and the **daily work plan** with its report, edits behind login. No command service, no backend: cannot move the robot |

Read-only API on both (GET, open): `/api/status`, `/api/telemetry`, `/api/events`, `/api/plans`, `/api/jobs`, `/api/orders`, `/api/cycles`, `/api/plan`, `/api/report`.
Write API on the monitoring host only, with the `X-Api-Key` header: jobs and plan rows. See [docs/API-REST.md](docs/API-REST.md).

**Customer ERP** (gestionale): article code files (one code per line) are read from `Erp:ImportFolder` every minute and deleted once stored (= acknowledgment); the codes are proposed in the job editor. `GET /api/erp/production` returns today's production per article code. See [docs/INTEGRAZIONE-GESTIONALE.md](docs/INTEGRAZIONE-GESTIONALE.md).

## Planning model

- **Job**: product/job code (unique, case-insensitive) → one robot plan, which is the whole machining of that code and runs exactly as programmed in Elements (the gateway resets the robot velocity override to 100 % before each plan). Several codes may use the same plan. Versioned, with history (who/when). A save on a stale version is refused (conflict warning).
- **Service code** (e.g. HOME): a job marked as service is always available to the operator, is never planned and never counted.
- **Daily work plan**: rows with day, code, quantity, optional order number and notes. Pending → Active (first cycle) → Completed (quantity reached), or Cancelled, or CarriedOver ("copy missing" moved the remaining pieces to another day). Only Pending rows can be edited or moved. The operator runs only open rows of today, in any order.
- **Cycle**: one run of the job's plan. Completed cycles count for their row; Stopped/Failed cycles are recorded but not counted.
- **Report**: per day, rows done / in progress / missing and pieces planned / done / missing (page `/orders`, API `/api/report`).

## Local workflow

1. Scan the barcode (USB keyboard-wedge: the scan field keeps the focus) or pick a row of today's plan / a service code.
   A scan selects the first open row of today with that code; a code not in today's plan is refused (service codes excepted). Scanning never moves the robot.
2. **Start…** → in-page confirmation (single-use token, 15 s).
3. Next cycles of the same row: **Start next cycle**, one press, as long as the previous cycle completed normally.
   After a fault, STOP or Stop the confirmation is required again.
4. The job version is read at each start: remote edits apply from the next cycle.

## Layout

```
FlexivManager.csproj             Blazor host
  Hosting/                       two-host composition, login endpoints, password hashing, demo seed
  Api/ReadOnlyApi.cs             GET-only JSON API (both hosts, open)
  Api/PlanningApi.cs             write API for jobs and the daily plan (monitoring host, API key)
  Components/Pages/              Operator (local), RobotStatus, RobotPlans, Jobs/JobEdit, Orders/OrderEdit
  Components/Planning/           login state, redirect to login
  Components/Robot/StopButton    software STOP (renders nothing without the command service)
src/RobotGateway.Core            no UI, no RDK, no database
  Planning/                      Job, ProductionOrder (plan row), Cycle, DailyReport, validators,
                                 PlanningService (remote edits), ScanService (local),
                                 CycleRecorder (persists cycle events)
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

REST write API: set a long random `Hosts:Monitoring:Api:Key` (e.g. environment variable `Hosts__Monitoring__Api__Key`)
and send it in the `X-Api-Key` header. Empty key = write API disabled. Development key: `dev-api-key`.

The database schema is created at first start (`EnsureCreated`); new columns are added at start-up by
`SqlitePlanningStore.EnsureCreated` (rows created before the daily plan get the day they were created on).
