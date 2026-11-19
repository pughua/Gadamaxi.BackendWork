# Gadamaxi.BackendWork

[简体中文](README.zh-CN.md) | **English**

> A background job framework built around one idea: a job is **a method plus a set of arguments**. Hand the framework an expression and a server process picks it up and runs it. Failures retry automatically, work recovers when a node dies, a large piece of work splits into a batch, and cross-service flows are orchestrated as a Saga with reverse-order compensation.

---

## Table of contents

1. [Requirements](#1-requirements)
2. [Installation](#2-installation)
3. [Quick start](#3-quick-start)
4. [Core usage](#4-core-usage)
5. [Configuration](#5-configuration)
6. [Dashboard](#6-dashboard)
7. [FAQ](#7-faq)

---

## 1. Requirements

| Item | Requirement |
|---|---|
| Target framework | .NET Core 3.0 / .NET 5 / 6 / 7 / 8 / 9 (the libraries target `netstandard2.1`) |
| Dashboard | **.NET 8 or later** + ASP.NET Core |
| Database | SQLite / SQL Server / MySQL (relational), or a MongoDB **replica set** |
| Database driver | **You must reference the ADO.NET provider yourself** — see [§2.2](#22-install-a-database-driver-required) |

---

## 2. Installation

### 2.1 Install the framework packages

Pick a storage backend first, then add the dashboard if you need it.

**Relational (SQLite / SQL Server / MySQL)**

```bash
dotnet add package Gadamaxi.BackendWork
dotnet add package Gadamaxi.BackendWork.Sql
```

```xml
<PackageReference Include="Gadamaxi.BackendWork" Version="0.1.0" />
<PackageReference Include="Gadamaxi.BackendWork.Sql" Version="0.1.0" />
```

**MongoDB**

```bash
dotnet add package Gadamaxi.BackendWork
dotnet add package Gadamaxi.BackendWork.Mongodb
```

```xml
<PackageReference Include="Gadamaxi.BackendWork" Version="0.1.0" />
<PackageReference Include="Gadamaxi.BackendWork.Mongodb" Version="0.1.0" />
```

**Dashboard (optional, requires .NET 8+)**

```xml
<PackageReference Include="Gadamaxi.BackendWork.Dashboard" Version="0.1.0" />
```

| Package | Purpose | Required |
|---|---|---|
| `Gadamaxi.BackendWork` | Core library | ✅ Required |
| `Gadamaxi.BackendWork.Sql` | Relational storage | One of Sql / Mongo |
| `Gadamaxi.BackendWork.Mongodb` | MongoDB storage | One of Sql / Mongo |
| `Gadamaxi.BackendWork.Dashboard` | Dashboard UI | Optional |

### 2.2 Install a database driver (required)

The storage packages **do not** ship the component that actually talks to the database. You have to add it yourself for whichever database you use. Skip this and you get `Cannot find provider` / `Unable to load ADO.NET provider` at runtime.

| Database | Package to install |
|---|---|
| SQLite | `Microsoft.Data.Sqlite` |
| SQL Server | `Microsoft.Data.SqlClient` |
| MySQL | `MySqlConnector` |

```xml
<!-- SQLite -->
<PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.12" />

<!-- SQL Server -->
<PackageReference Include="Microsoft.Data.SqlClient" Version="7.1.1" />
```

> With MongoDB you can skip this step — `MongoDB.Driver` comes in automatically with the framework package.

### 2.3 Add namespaces

```csharp
using Gadamaxi.BackendWork;              // WorkHelper
using Gadamaxi.BackendWork.Server;       // WorkServerOption, Helper
using Gadamaxi.BackendWork.Works;        // IWorkManager, Workload
using Gadamaxi.BackendWork.Recurrings;   // IRecurringWorkManager, RecurringWorkContext
using Gadamaxi.BackendWork.Batchs;       // IBatchManager, BatchContext
using Gadamaxi.BackendWork.Sagas;        // ISagaManager, Saga<TSaga>

// Storage backends
using Gadamaxi.BackendWork.Sql;          // UseSqlBackendWork
using Gadamaxi.BackendWork.Mongodb;      // UseMongodbBackendWork

// Dashboard (optional)
using Gadamaxi.BackendWork.Dashboard;    // AddBackendWorkDashboard, MapBackendWorkDashboard
```

---

## 3. Quick start

### 3.1 ASP.NET Core + SQLite (minimal working example)

```csharp
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Sql;
using LinqToDB;

var builder = WebApplication.CreateBuilder(args);

// 1. Register the framework
builder.Services.AddBackendWork(
    new WorkServerOption
    {
        ServerName       = "my-app-node-1",
        UseBackendServer = true,   // this node both enqueues and executes jobs
        WorkerCount      = 8       // concurrency; 0 = automatic
    },
    config =>
    {
        var dataOptions = new DataOptions().UseSQLite("Data Source=backendwork.db;");
        config.UseSqlBackendWork(builder.Services, () => dataOptions);
    });

var app = builder.Build();

// 2. Create the schema (required for relational databases)
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider
         .GetRequiredService<FluentMigrator.Runner.IMigrationRunner>()
         .MigrateUp();
}

// 3. Enqueue a job
app.MapPost("/hello", async () =>
{
    var workId = await WorkHelper.Manager.Enqueue(
        () => Console.WriteLine($"hello {DateTime.UtcNow}"),
        "demo.hello");
    return Results.Ok(new { workId });
});

app.Run();
```

> ⚠️ The schema step must run inside `CreateScope()`, otherwise you get `Cannot resolve scoped service from root provider`.

### 3.2 ASP.NET Core + MongoDB

```csharp
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Mongodb;
using Gadamaxi.BackendWork.Server;

builder.Services.AddBackendWork(
    new WorkServerOption { ServerName = "my-app-node-1", UseBackendServer = true },
    config => config.UseMongodbBackendWork(builder.Services,
        () => new MongoBackendWorkOptions(
            connectionString: "mongodb://127.0.0.1:27017/?replicaSet=rs0",
            databaseName:     "backendwork")));
```

MongoDB needs no schema step (collections are created on first write) and the framework builds its indexes on first connection. If you would rather surface connection problems at startup, resolve the context once:

```csharp
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<MongoBackendWorkContext>();
}
```

> ⚠️ MongoDB **must be a replica set or a sharded cluster**. Standalone mode does not support multi-document transactions, and the framework degrades to per-document writes with no atomicity. In production, set `RequireTransactions = true` so a misconfiguration fails at startup instead of silently degrading.

### 3.3 Console / Worker Service

```csharp
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Sql;
using LinqToDB;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddBackendWork(
            new WorkServerOption { ServerName = "worker-1", UseBackendServer = true },
            config => config.UseSqlBackendWork(services,
                () => new DataOptions().UseSQLite("Data Source=backendwork.db;")));
    })
    .Build();

await host.RunAsync();
```

---

## 4. Core usage

All four entry points live on `WorkHelper`:

```csharp
WorkHelper.Manager    // immediate / delayed / continuation jobs
WorkHelper.Recurring  // recurring jobs
WorkHelper.Batch      // batch jobs
WorkHelper.Saga       // distributed transaction orchestration
```

> ⚠️ These four are only usable **after the application has finished starting** (controllers, minimal API handlers, background services are all fine). Calling them before `builder.Build()` throws `InvalidOperationException`. If you need to enqueue during startup, resolve `IWorkManager` from `app.Services`.

### 4.1 Immediate jobs

Drop it in the queue; it runs right away.

```csharp
// No arguments
var id = await WorkHelper.Manager.Enqueue(
    () => Console.WriteLine("hello"),
    "demo.hello");

// Calling a dependency-injected service
var id2 = await WorkHelper.Manager.Enqueue<IMailService>(
    s => s.SendAsync("a@b.c", "Subject"),
    "mail.send");

// Target a specific queue
var id3 = await WorkHelper.Manager.Enqueue(
    () => Console.WriteLine("hi"),
    "demo.high",
    queue: "high");
```

### 4.2 Delayed jobs

They enter the queue only when the delay has elapsed.

```csharp
await WorkHelper.Manager.Schedule(
    () => Console.WriteLine("runs in 10 minutes"),
    TimeSpan.FromMinutes(10),
    "demo.delayed");
```

> `Schedule` has no `queue` parameter. If you need one, build a `Workload` yourself and set `.Queue`.

### 4.3 Continuations (job chains)

A child job is only woken up after its parent succeeds; chains can be as long as you like.

```csharp
var parentId = await WorkHelper.Manager.Enqueue(() => Step1Async(), "flow.step1");

// Runs automatically once the parent succeeds
var childId = await WorkHelper.Manager.Awaiting(() => Step2Async(), parentId, "flow.step2");

// Keep chaining
await WorkHelper.Manager.Awaiting(() => Step3Async(), childId, "flow.step3");
```

> ⚠️ If a parent **exhausts its retries and ends up failed**, its continuations are never woken up. That is deliberate — it covers the "don't continue when the main flow failed" case.

### 4.4 Recurring jobs (Cron)

```csharp
using Gadamaxi.BackendWork.Recurrings;

await WorkHelper.Recurring.AddOrUpdate(
    recurringWorkId: "nightly-report",
    work:            Workload.FromExpression(() => Console.WriteLine("nightly report")),
    cornExpression:  "0 2 * * *",                       // every day at 02:00
    option:          new RecurringWorkContext
    {
        TimeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai")
    },
    name:            "Report generation");

await WorkHelper.Recurring.Trigger("nightly-report");        // run once now
await WorkHelper.Recurring.RemoveIfExists("nightly-report"); // take it offline
```

Cron expressions use the standard 5 fields (minute hour day month weekday). Six-field expressions with seconds are also supported, as are shorthands such as `@daily` and `@hourly`. `TimeZoneInfo` decides which time zone the expression is interpreted in; **if you leave it unset, the expression is treated as UTC**.

### 4.5 Batch jobs

Create a set of child jobs in one go; the framework rolls their actual outcomes up into the batch status.

```csharp
var batchId = await WorkHelper.Batch.Create(batch =>
{
    for (var i = 1; i <= 10; i++)
    {
        var n = i;                              // <- copy to a local, see the note below
        batch.Enqueue(() => Console.WriteLine($"child job {n}"));
    }
}, "monthly reconciliation");
```

- Child job names are derived automatically: `monthly reconciliation#1` … `monthly reconciliation#10`.
- A batch name is capped at 192 characters (room has to be left for the `#n` suffix).
- `Enqueue` / `Schedule` / `Awaiting` on `BatchContext` work the same way as on `WorkHelper.Manager`, but they **return `string` directly** (registration is synchronous, no `await` needed).

Run something after an existing batch:

```csharp
var nextBatchId = await WorkHelper.Batch.CreateContinue(batchId, batch =>
{
    batch.Enqueue(() => Console.WriteLine("runs once the previous batch finishes"));
}, "reconciliation review");

// Retry the failed child jobs of a batch
await WorkHelper.Batch.Retry(batchId);
```

> ⚠️ Inside a loop you must write `var n = i;`. C# `for` loop variables are shared in expression trees, so `() => F(i)` would give every child job the final value.

### 4.6 Distributed transactions (Saga)

Steps execute forward one by one; if any step fails, times out, or is cancelled, compensation runs in **reverse order**.

#### Define

```csharp
using Gadamaxi.BackendWork.Sagas;

public class OrderSaga : Saga<OrderSaga>
{
    // Business input: snapshotted when the saga starts
    public string  OrderNo { get; set; } = "";
    public decimal Amount  { get; set; }

    // Idempotency key: starting again with the same CorrelationId does not create a second saga
    public override string CorrelationId => OrderNo;

    protected override void Configure(SagaDefinition<OrderSaga> def)
    {
        // Each step = a forward action plus its compensation
        def.Step(s => s.DeductInventory(s.OrderNo),
                 s => s.UndoDeductInventory());

        def.Step(s => s.ChargePayment(s.Amount, "CNY"),
                 s => s.RefundPayment());

        // A step with nothing to compensate (e.g. sending a notification)
        def.StepNoCompensate(s => s.SendNotification());

        def.SetVariable("channel", "web");
    }

    public Task DeductInventory(string orderNo)        => Task.CompletedTask;
    public Task UndoDeductInventory()                  => Task.CompletedTask;
    public Task ChargePayment(decimal amt, string cur) => Task.CompletedTask;
    public Task RefundPayment()                        => Task.CompletedTask;
    public Task SendNotification()                     => Task.CompletedTask;
}
```

#### Start and manage

```csharp
var sagaId = await WorkHelper.Saga.Start(new OrderSaga
{
    OrderNo = "SO20261115001",
    Amount  = 199.00m
});

await WorkHelper.Saga.Cancel(sagaId, "cancelled by user");  // cancel -> triggers compensation
await WorkHelper.Saga.RetryCompensation(sagaId);           // retry after a compensation failure
var saga  = await WorkHelper.Saga.GetSaga(sagaId);         // query status
var steps = await WorkHelper.Saga.GetSteps(sagaId);        // query individual steps
```

#### State transitions

```
None ──start──▶ Running ──all steps succeeded──▶ Completed
                  │
        a step failed / timed out / cancelled
                  ▼
             Compensating ──all compensations ok──▶ Compensated
                  │
        compensation retries exhausted
                  ▼
               Aborted
```

#### Four things to watch when writing a Saga

1. **The input is a snapshot.** Mutating the object after the saga starts has no effect — the framework restores a fresh instance from the snapshot when it executes.
2. **Values cross steps only via `SetVariable` or `outputKey`.** Changing properties on `this` inside a step method is not persisted.
3. **Expressions with arguments must use member access**: write `s => s.Consume(s.OrderNo)`, not `s => s.Consume(OrderNo)`.
4. **Step methods cannot have optional parameters** (compiler error `CS0854`); pass every argument explicitly.

### 4.7 Requirements for job methods

A job is eventually serialized to JSON, stored in the database, and rehydrated on the server by reflection. That imposes a few constraints:

| Requirement | Notes |
|---|---|
| Arguments must round-trip through JSON | Use simple types, DTOs, or arrays — not objects like `Stream` or `DbContext` |
| Do not capture closure variables | Only the method call and constant arguments are persisted; captured values are not carried over |
| The method must be resolvable | Usually a method on a service registered in the container, called as `Enqueue<IMyService>(s => s.Do())` |
| Sync and async both work | `void` / `Task` / `Task<T>` are all supported |
| Job name is mandatory, ≤ 200 characters | Used for dashboard search and log correlation |

---

## 5. Configuration

### 5.1 `WorkServerOption`

| Property | Default | Description |
|---|---|---|
| `ServerName` | `""` | Node name. **Must be unique across a multi-node deployment** |
| `UseBackendServer` | `true` | Whether this node executes jobs. Set to `false` for enqueue-only nodes (e.g. a pure web front end) |
| `Queues` | `["defaultqueue"]` | Queues this node consumes |
| `WorkerCount` | `0` (auto) | Number of concurrent executions. `0` resolves to `Min(CPU×2, 15)` |
| `BatchSize` | `10` | Maximum jobs handled per polling round |
| `WorkRetryCount` | `5` | Retries after a single job fails |
| `WorkRetryDelay` | `1 minute` | Delay between retries |
| `BatchRetryCount` | `3` | Retries for the batch as a whole |
| `BatchExpireTime` | `7 days` | How long a finished batch is retained (then cleaned up automatically) |
| `WorkPollingDelay` | `15 seconds` | Queue polling interval |
| `SchedulePollingDelay` | `1 minute` | Scan interval for delayed jobs |
| `RecurringPollingDelay` | `1 minute` | Scan interval for recurring jobs |
| `HeartbeatDelay` | `5 minutes` | Node heartbeat interval |
| `ServerDeadThreshold` | `25 minutes` | How long without a heartbeat before a node counts as offline |
| `WorkProcessingTimeout` | `10 minutes` | A job running longer than this with no one holding its lock is considered stuck and re-enqueued |
| `RecoveryPollingDelay` | `1 minute` | Scan interval for recovering jobs from stuck or offline nodes |
| `WorkIntervalDelay` | `100 milliseconds` | Gap between jobs in the same poll (for smoothing spikes) |
| `CleanupPollingDelay` | `1 hour` | Interval for purging expired data |
| `SagaPollingDelay` | `1 second` | Saga advancement polling interval |
| `SagaTimeout` | `30 minutes` | Overall saga timeout; on expiry the saga moves to compensation |
| `SagaTimeoutPollingDelay` | `1 minute` | Scan interval for saga timeouts |
| `SagaExpireTime` | `7 days` | How long a finished saga is retained |
| `ShutdownTimeout` | `15 seconds` | How long shutdown waits for in-flight jobs |

### 5.2 `MongoBackendWorkOptions` (MongoDB only)

| Property | Default | Description |
|---|---|---|
| `connectionString` (constructor argument) | — | Connection string, cannot be empty |
| `databaseName` (constructor argument) | — | Database name, cannot be empty |
| `CollectionPrefix` | `""` | Collection name prefix (useful when several environments share one database) |
| `RequireTransactions` | `false` | When `true`, a non-replica-set environment fails at startup. **Recommended in production** |
| `ConfigureClient` | `null` | Customize MongoDB client settings (timeouts, connection pool, etc.) |

### 5.3 `BackendWorkDashboardOptions`

| Property | Default | Description |
|---|---|---|
| `RoutePrefix` | `"/backendwork-dashboard"` | Path the dashboard is served from |
| `AuthorizationPolicy` | `null` | Policy required to view the dashboard. `null` means "any authenticated user" |
| `ReadOnly` | `true` | Read-only mode. **Recommended to keep `true` in production** |
| `EnableWriteOperations` | `false` | Whether retry / cancel / trigger actions are available in the UI |
| `WritePolicy` | `null` | Policy required for write operations |
| `SlowTaskThresholdSeconds` | `60` | Jobs running longer than this are flagged as slow |
| `NodeOfflineThresholdSeconds` | `60` | Node-offline threshold (automatically raised to twice the heartbeat interval) |
| `StalledSagaThresholdMinutes` | `30` | Threshold for flagging a stalled saga |
| `RedactSensitiveData` | `true` | Whether to redact sensitive values in job arguments |
| `RedactMaxLength` | `2000` | Truncation length applied before redaction |

### 5.4 Configuration example

```csharp
builder.Services.AddBackendWork(
    new WorkServerOption
    {
        ServerName           = Environment.MachineName,
        UseBackendServer     = true,
        WorkerCount          = 16,
        Queues               = new List<string> { "defaultqueue", "high" },
        WorkRetryCount       = 3,
        WorkRetryDelay       = TimeSpan.FromSeconds(30),
        WorkProcessingTimeout = TimeSpan.FromMinutes(30),
        ShutdownTimeout      = TimeSpan.FromSeconds(30)
    },
    config => config.UseSqlBackendWork(builder.Services, () => dataOptions));
```

---

## 6. Dashboard

### 6.1 Setup

Three steps, all required.

```csharp
using Gadamaxi.BackendWork.Dashboard;
using Gadamaxi.BackendWork.Sql;

// 1. Register the dashboard (after AddBackendWork)
builder.Services.AddBackendWorkDashboard(options =>
{
    options.RoutePrefix   = "/backendwork-dashboard";
    options.ReadOnly      = true;              // read-only is recommended in production
});

// 2. Register a data source (relational or MongoDB, pick one)
builder.Services.AddSqlDashboardRepository();
// or builder.Services.AddMongodbDashboardRepository();

var app = builder.Build();

app.UseAuthentication();     // <- must come before the mapping call
app.UseAuthorization();

app.MapBackendWorkDashboard();   // 3. mount it
```

> ⚠️ Skip step 2 and `MapBackendWorkDashboard()` throws `InvalidOperationException`.
>
> ⚠️ Step 3 must be called **after** `UseAuthentication()` / `UseAuthorization()`, otherwise your authorization policies are not applied.

### 6.2 Authorization

The dashboard does not implement login itself — it uses whatever authentication your app already has. When `AuthorizationPolicy` is `null`, an authenticated user is required.

```csharp
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("DashboardViewer",  p => p.RequireAuthenticatedUser())
    .AddPolicy("BackendWorkAdmin", p => p.RequireRole("Admin"));
```

```csharp
builder.Services.AddBackendWorkDashboard(options =>
{
    options.AuthorizationPolicy   = "DashboardViewer";
    options.ReadOnly              = false;
    options.EnableWriteOperations = true;
    options.WritePolicy           = "BackendWorkAdmin";
});
```

> ⚠️ **An unauthenticated request returns 404** (not 401/403). That is standard authentication-middleware behavior. Sign in first, then browse to the dashboard.

### 6.3 What you get

Browse to `{RoutePrefix}` to open the dashboard. It includes:

- **Overview**: total job count, breakdown by state, node count
- **Jobs**: filter by state / name / queue, inspect details and failure reasons
- **Concurrency**: jobs currently executing
- **Recurring**: definitions, execution history, next run time
- **Batches**: batch list and per-child-job status
- **Saga**: transaction list, step-by-step progress, stall warnings
- **Nodes**: heartbeats, consumed queues, concurrency usage

With write operations enabled, the UI also exposes retry, cancel, trigger-now, pause, and resume actions.

### 6.4 HTTP API

The dashboard also serves an HTTP API under `{RoutePrefix}/api` that you can wire straight into your own ops tooling.

| Method | Path | Description |
|---|---|---|
| GET | `/overview` | Summary statistics |
| GET | `/tasks` | Job list (supports `state` / `name` / `queue` / paging) |
| GET | `/tasks/{id}` | Job details |
| GET | `/concurrency` | Current concurrent executions |
| GET | `/recurring` | Recurring job list |
| GET | `/recurring/{id}/history` | Recurring job execution history |
| GET | `/batches` | Batch list |
| GET | `/batches/{id}` | Batch details |
| GET | `/sagas` | Saga list (supports `status` / `stalled`) |
| GET | `/sagas/{id}` | Saga details |
| GET | `/nodes` | Node list |
| GET | `/nodes/{name}` | Node details |
| POST | `/tasks/{id}/retry` | Retry a job |
| POST | `/tasks/{id}/cancel` | Cancel a job |
| POST | `/batches/{id}/retry` | Retry a batch |
| POST | `/recurring/{id}/trigger` | Trigger now |
| POST | `/recurring/{id}/pause` | Pause |
| POST | `/recurring/{id}/resume` | Resume |

> Every `POST` endpoint requires `EnableWriteOperations = true` and a caller that satisfies `WritePolicy`.

---

## 7. FAQ

**Q: `Cannot find provider` / `Unable to load ADO.NET provider` at startup**

The database driver is missing. Add `Microsoft.Data.Sqlite` (or `Microsoft.Data.SqlClient` / `MySqlConnector`) as described in [§2.2](#22-install-a-database-driver-required).

**Q: `WorkHelper.Manager` throws `InvalidOperationException`**

You called it before `builder.Build()`. Move the call to after startup (controllers, minimal APIs, background services), or resolve `IWorkManager` from `app.Services`.

**Q: Jobs are enqueued but never run**

Check in order: (1) is `UseBackendServer` set to `true`; (2) is the queue you enqueued to listed in `Queues`; (3) did the relational database get its schema (`MigrateUp()`); (4) can the app reach the database.

**Q: A job stays "running" forever**

By default a job running longer than 10 minutes with no node holding its lock is automatically re-enqueued. If it never recovers, check that node heartbeats are healthy and that no node exited abnormally. Lowering `WorkProcessingTimeout` speeds recovery up.

**Q: SQLite says `Connection string keyword 'xxx' is not supported`**

A SQLite connection string only accepts these keywords: `Data Source`, `Mode`, `Cache`, `Password`, `Foreign Keys`, `Recursive Triggers`, `Default Timeout`, `Pooling`. Other keywords (such as `BusyTimeout`, `Journal Mode`, `DateTimeKind`) are dropped by the framework, but you will get an error if you pass the raw string to a connection yourself.

To enable WAL, run this once at database-creation time (WAL is a persistent property stored in the database file, it cannot be set through a connection string):

```csharp
using var conn = new SqliteConnection("Data Source=backendwork.db;");
conn.Open();
using var cmd = conn.CreateCommand();
cmd.CommandText = "PRAGMA journal_mode=WAL;";
cmd.ExecuteNonQuery();
```

**Q: MongoDB throws `Transaction numbers are only allowed on a replica set member or mongos`**

Standalone mode does not support transactions. Deploy a replica set (a single-member replica set is fine for development), or accept the non-transactional degraded mode.

**Q: A Saga never advances**

(1) Advancement polls once per second, so give it a moment; (2) check whether a node exited abnormally while still holding that saga's lock (the lock times out after one minute); (3) open the saga detail page in the dashboard and look for the stalled marker.

**Q: The dashboard returns 404**

Unauthenticated requests are short-circuited to 404 by the authentication middleware. Sign in first, then browse to `{RoutePrefix}`.

**Q: `MapBackendWorkDashboard()` throws `InvalidOperationException`**

You forgot to register a data source. Add `AddSqlDashboardRepository()` or `AddMongodbDashboardRepository()`.
