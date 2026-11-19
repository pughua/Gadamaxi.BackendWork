# Gadamaxi.BackendWork 使用说明

**简体中文** | [English](README.md)

> 后台任务框架：任务是**"一个方法 + 一组参数"**。把表达式交给框架，由服务端进程取出来执行；失败自动重试，节点挂了自动恢复，一批活能拆批量，跨服务流程能走 Saga 编排并逆序补偿。

---

## 目录

1. [环境要求](#1-环境要求)
2. [安装](#2-安装)
3. [快速开始](#3-快速开始)
4. [核心用法](#4-核心用法)
5. [配置项](#5-配置项)
6. [监控面板](#6-监控面板)
7. [常见问题](#7-常见问题)

---

## 1. 环境要求

| 项目 | 要求 |
|---|---|
| 项目框架 | .NET Core 3.0 / .NET 5 / 6 / 7 / 8 / 9（框架库为 `netstandard2.1`） |
| 监控面板 | 需 **.NET 8 及以上** + ASP.NET Core |
| 数据库 | SQLite / SQL Server / MySQL（关系库），或 MongoDB **副本集** |
| 数据库驱动 | **必须自行引用** ADO.NET provider，见 [§2.2](#22-安装数据库驱动必做) |

---

## 2. 安装

### 2.1 安装框架包

先选存储后端，再按需加监控面板。

**关系库（SQLite / SQL Server / MySQL）**

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

**监控面板（可选，需 .NET 8+）**

```xml
<PackageReference Include="Gadamaxi.BackendWork.Dashboard" Version="0.1.0" />
```

| 包 | 用途 | 必需性 |
|---|---|---|
| `Gadamaxi.BackendWork` | 核心库 | ✅ 必需 |
| `Gadamaxi.BackendWork.Sql` | 关系库存储 | 与 Mongo 二选一 |
| `Gadamaxi.BackendWork.Mongodb` | MongoDB 存储 | 与 Sql 二选一 |
| `Gadamaxi.BackendWork.Dashboard` | 监控面板 | 可选 |

### 2.2 安装数据库驱动（必做）

框架的数据库驱动包**不包含**实际连接数据库的组件，需要你按所用数据库自行添加。漏装这一项，运行时会报 `Cannot find provider` / `Unable to load ADO.NET provider`。

| 数据库 | 需要安装的包 |
|---|---|
| SQLite | `Microsoft.Data.Sqlite` |
| SQL Server | `Microsoft.Data.SqlClient` |
| MySQL | `MySqlConnector` |

```xml
<!-- SQLite 示例 -->
<PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.12" />

<!-- SQL Server 示例 -->
<PackageReference Include="Microsoft.Data.SqlClient" Version="7.1.1" />
```

> 使用 MongoDB 时无需这一步，`MongoDB.Driver` 已随框架包自动带入。

### 2.3 添加命名空间

```csharp
using Gadamaxi.BackendWork;              // WorkHelper
using Gadamaxi.BackendWork.Server;       // WorkServerOption, Helper
using Gadamaxi.BackendWork.Works;        // IWorkManager, Workload
using Gadamaxi.BackendWork.Recurrings;   // IRecurringWorkManager, RecurringWorkContext
using Gadamaxi.BackendWork.Batchs;       // IBatchManager, BatchContext
using Gadamaxi.BackendWork.Sagas;        // ISagaManager, Saga<TSaga>

// 存储后端
using Gadamaxi.BackendWork.Sql;          // UseSqlBackendWork
using Gadamaxi.BackendWork.Mongodb;      // UseMongodbBackendWork

// 监控面板（可选）
using Gadamaxi.BackendWork.Dashboard;    // AddBackendWorkDashboard, MapBackendWorkDashboard
```

---

## 3. 快速开始

### 3.1 ASP.NET Core + SQLite（最小可运行）

```csharp
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Sql;
using LinqToDB;

var builder = WebApplication.CreateBuilder(args);

// ① 注册框架
builder.Services.AddBackendWork(
    new WorkServerOption
    {
        ServerName       = "my-app-node-1",
        UseBackendServer = true,   // 本节点同时投递并执行任务
        WorkerCount      = 8       // 并发度，0 = 自动
    },
    config =>
    {
        var dataOptions = new DataOptions().UseSQLite("Data Source=backendwork.db;");
        config.UseSqlBackendWork(builder.Services, () => dataOptions);
    });

var app = builder.Build();

// ② 建表（关系库必做）
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider
         .GetRequiredService<FluentMigrator.Runner.IMigrationRunner>()
         .MigrateUp();
}

// ③ 投递任务
app.MapPost("/hello", async () =>
{
    var workId = await WorkHelper.Manager.Enqueue(
        () => Console.WriteLine($"hello {DateTime.UtcNow}"),
        "demo.hello");
    return Results.Ok(new { workId });
});

app.Run();
```

> ⚠️ 建表这一步必须在 `CreateScope()` 内执行，否则报 `Cannot resolve scoped service from root provider`。

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

MongoDB 无需建表（集合首次写入时自动创建），框架会在首次连接时自动建索引。若想在启动阶段就暴露连接问题，可解析一次上下文：

```csharp
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<MongoBackendWorkContext>();
}
```

> ⚠️ MongoDB **必须是副本集或分片集群**。单机模式不支持多文档事务，框架会降级为逐条写入（无原子性）。生产环境建议设置 `RequireTransactions = true`，让配置错误在启动时直接报错。

### 3.3 控制台 / Worker Service

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

## 4. 核心用法

四个入口都在 `WorkHelper` 上：

```csharp
WorkHelper.Manager    // 即时 / 延时 / 连续任务
WorkHelper.Recurring  // 周期任务
WorkHelper.Batch      // 批量任务
WorkHelper.Saga       // 分布式事务编排
```

> ⚠️ 这四个入口只能在**应用启动完成之后**使用（控制器、Minimal API 处理器、后台服务里都可以）。在 `builder.Build()` 之前调用会抛 `InvalidOperationException`。若需在启动阶段投递任务，请从 `app.Services` 取 `IWorkManager`。

### 4.1 即时任务

丢进队列，立刻被执行。

```csharp
// 无参
var id = await WorkHelper.Manager.Enqueue(
    () => Console.WriteLine("hello"),
    "demo.hello");

// 需要依赖注入的服务
var id2 = await WorkHelper.Manager.Enqueue<IMailService>(
    s => s.SendAsync("a@b.c", "标题"),
    "mail.send");

// 投到指定队列
var id3 = await WorkHelper.Manager.Enqueue(
    () => Console.WriteLine("hi"),
    "demo.high",
    queue: "high");
```

### 4.2 延时任务

到点之后才进入队列。

```csharp
await WorkHelper.Manager.Schedule(
    () => Console.WriteLine("10 分钟后执行"),
    TimeSpan.FromMinutes(10),
    "demo.delayed");
```

> 延时任务没有 `queue` 参数。需要指定队列时，自己构造 `Workload` 并设置 `.Queue`。

### 4.3 连续任务（任务链）

父任务成功后才被唤醒，可以多级串联。

```csharp
var parentId = await WorkHelper.Manager.Enqueue(() => Step1Async(), "flow.step1");

// 父任务成功后自动执行
var childId = await WorkHelper.Manager.Awaiting(() => Step2Async(), parentId, "flow.step2");

// 继续往下串
await WorkHelper.Manager.Awaiting(() => Step3Async(), childId, "flow.step3");
```

> ⚠️ 父任务**重试耗尽、最终失败**时，它的连续子任务不会被唤醒。这是有意设计的（用于"主流程失败就不继续"的场景）。

### 4.4 周期任务（Cron）

```csharp
using Gadamaxi.BackendWork.Recurrings;

await WorkHelper.Recurring.AddOrUpdate(
    recurringWorkId: "nightly-report",
    work:            Workload.FromExpression(() => Console.WriteLine("每日报表")),
    cornExpression:  "0 2 * * *",                       // 每天 02:00
    option:          new RecurringWorkContext
    {
        TimeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai")
    },
    name:            "报表生成");

await WorkHelper.Recurring.Trigger("nightly-report");        // 手动触发一次
await WorkHelper.Recurring.RemoveIfExists("nightly-report"); // 下线
```

Cron 表达式为标准 5 段（分 时 日 月 周），另支持带秒的 6 段以及 `@daily`、`@hourly` 等简写。`TimeZoneInfo` 决定表达式按哪个时区解释，**不设置则按 UTC**。

### 4.5 批量任务

一次创建一批子任务，框架按子任务的实际结果收敛批量状态。

```csharp
var batchId = await WorkHelper.Batch.Create(batch =>
{
    for (var i = 1; i <= 10; i++)
    {
        var n = i;                              // ← 必须做局部副本，见下方说明
        batch.Enqueue(() => Console.WriteLine($"子任务 {n}"));
    }
}, "月结对账");
```

- 子任务名自动派生为 `月结对账#1` … `月结对账#10`。
- 批量名最长 192 字符（要给 `#序号` 后缀留位置）。
- `BatchContext` 上的 `Enqueue` / `Schedule` / `Awaiting` 用法与 `WorkHelper.Manager` 相同，但**直接返回 `string`**（同步登记，无需 `await`）。

接在已有批量之后执行：

```csharp
var nextBatchId = await WorkHelper.Batch.CreateContinue(batchId, batch =>
{
    batch.Enqueue(() => Console.WriteLine("上一批完成后执行"));
}, "对账复核");

// 重试失败的批量
await WorkHelper.Batch.Retry(batchId);
```

> ⚠️ 循环里构造表达式必须写 `var n = i;`。C# 的 `for` 循环变量在表达式树里是共享的，直接写 `() => F(i)` 会让所有子任务都拿到最终值。

### 4.6 分布式事务（Saga）

正向逐步执行；任一步失败、超时或被取消，就按**逆序**依次补偿。

#### 定义

```csharp
using Gadamaxi.BackendWork.Sagas;

public class OrderSaga : Saga<OrderSaga>
{
    // 业务输入：启动时会被序列化为快照
    public string  OrderNo { get; set; } = "";
    public decimal Amount  { get; set; }

    // 幂等键：同一个 CorrelationId 重复启动不会创建第二个 saga
    public override string CorrelationId => OrderNo;

    protected override void Configure(SagaDefinition<OrderSaga> def)
    {
        // 每步 = 正向动作 + 补偿动作
        def.Step(s => s.DeductInventory(s.OrderNo),
                 s => s.UndoDeductInventory());

        def.Step(s => s.ChargePayment(s.Amount, "CNY"),
                 s => s.RefundPayment());

        // 无需补偿的步骤（如发通知）
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

#### 启动与管理

```csharp
var sagaId = await WorkHelper.Saga.Start(new OrderSaga
{
    OrderNo = "SO20261115001",
    Amount  = 199.00m
});

await WorkHelper.Saga.Cancel(sagaId, "用户取消");      // 取消 → 触发补偿
await WorkHelper.Saga.RetryCompensation(sagaId);     // 补偿失败后重试
var saga  = await WorkHelper.Saga.GetSaga(sagaId);   // 查询状态
var steps = await WorkHelper.Saga.GetSteps(sagaId);  // 查询各步骤
```

#### 状态流转

```
未开始 ──启动──▶ 执行中 ──全部成功──▶ 已完成
                    │
           某步失败 / 超时 / 被取消
                    ▼
                补偿中 ──补偿全部成功──▶ 已补偿
                    │
              补偿重试耗尽
                    ▼
                 已中止
```

#### 写 Saga 时的四个注意点

1. **输入是快照**。启动之后再改对象属性没有任何效果。执行时框架会用快照还原一个新实例。
2. **跨步骤传值只能靠 `SetVariable` 或 `outputKey`**。在步骤方法里改 `this` 的属性不会被保存。
3. **带参数的表达式要写成成员访问形式**：`s => s.Consume(s.OrderNo)`，不能写 `s => s.Consume(OrderNo)`。
4. **步骤方法不能有可选参数**（会编译报 `CS0854`），必须把所有实参写全。

### 4.7 任务函数的写法要求

任务最终会被序列化成 JSON 存进数据库，再在服务端反射还原执行，因此：

| 要求 | 说明 |
|---|---|
| 参数必须可 JSON 往返 | 用简单类型、DTO 或数组，不要传 `Stream`、`DbContext` 这类对象 |
| 不要捕获闭包变量 | 只有方法调用和常量参数会被保存，闭包里的变量值不会带过去 |
| 方法要能被解析 | 通常写在已注册到容器的服务上，通过 `Enqueue<IMyService>(s => s.Do())` 调用 |
| 同步、异步都支持 | `void` / `Task` / `Task<T>` 均可 |
| 任务名必传，≤ 200 字符 | 用于监控面板检索与日志定位 |

---

## 5. 配置项

### 5.1 `WorkServerOption`

| 属性 | 默认值 | 说明 |
|---|---|---|
| `ServerName` | `""` | 节点名。**多节点部署必须各不相同** |
| `UseBackendServer` | `true` | 是否执行任务。设为 `false` 表示只投递、不消费（如纯 Web 前端节点） |
| `Queues` | `["defaultqueue"]` | 本节点消费的队列列表 |
| `WorkerCount` | `0`（自动） | 并发执行数。`0` 时取 `Min(CPU×2, 15)` |
| `BatchSize` | `10` | 每轮最多处理多少条 |
| `WorkRetryCount` | `5` | 单个任务失败后的重试次数 |
| `WorkRetryDelay` | `1 分钟` | 两次重试之间的间隔 |
| `BatchRetryCount` | `3` | 批量整体重试次数 |
| `BatchExpireTime` | `7 天` | 已结束批量保留多久（之后自动清理） |
| `WorkPollingDelay` | `15 秒` | 队列轮询间隔 |
| `SchedulePollingDelay` | `1 分钟` | 延时任务扫描间隔 |
| `RecurringPollingDelay` | `1 分钟` | 周期任务扫描间隔 |
| `HeartbeatDelay` | `5 分钟` | 节点心跳间隔 |
| `ServerDeadThreshold` | `25 分钟` | 多久没心跳算节点已下线 |
| `WorkProcessingTimeout` | `10 分钟` | 任务执行超过该时长且无人持锁，判定卡死并重新入队 |
| `RecoveryPollingDelay` | `1 分钟` | 恢复卡死/下线节点任务的扫描间隔 |
| `WorkIntervalDelay` | `100 毫秒` | 同一批任务之间的处理间隔（削峰用） |
| `CleanupPollingDelay` | `1 小时` | 过期数据清理间隔 |
| `SagaPollingDelay` | `1 秒` | Saga 推进轮询间隔 |
| `SagaTimeout` | `30 分钟` | Saga 整体超时，超时后转补偿 |
| `SagaTimeoutPollingDelay` | `1 分钟` | Saga 超时扫描间隔 |
| `SagaExpireTime` | `7 天` | 已结束的 Saga 保留多久 |
| `ShutdownTimeout` | `15 秒` | 停机时等待在途任务的最长时间 |

### 5.2 `MongoBackendWorkOptions`（仅 MongoDB）

| 属性 | 默认值 | 说明 |
|---|---|---|
| `connectionString`（构造参数） | — | 连接串，不可为空 |
| `databaseName`（构造参数） | — | 库名，不可为空 |
| `CollectionPrefix` | `""` | 集合名前缀（多套环境共用同一库时使用） |
| `RequireTransactions` | `false` | 设为 `true` 时，非副本集环境会在启动时直接报错。**生产建议开启** |
| `ConfigureClient` | `null` | 自定义 MongoDB 客户端设置（超时、连接池等） |

### 5.3 `BackendWorkDashboardOptions`（监控面板）

| 属性 | 默认值 | 说明 |
|---|---|---|
| `RoutePrefix` | `"/backendwork-dashboard"` | 面板访问路径 |
| `AuthorizationPolicy` | `null` | 访问面板所需策略。`null` 表示要求用户已登录 |
| `ReadOnly` | `true` | 只读模式。**生产环境建议保持 `true`** |
| `EnableWriteOperations` | `false` | 是否允许在面板上重试/取消/触发任务 |
| `WritePolicy` | `null` | 写操作所需策略 |
| `SlowTaskThresholdSeconds` | `60` | 超过该时长的任务标记为"慢任务" |
| `NodeOfflineThresholdSeconds` | `60` | 节点失联判定阈值（会自动抬到心跳间隔的 2 倍） |
| `StalledSagaThresholdMinutes` | `30` | Saga 停滞判定阈值 |
| `RedactSensitiveData` | `true` | 是否对任务参数中的敏感信息脱敏 |
| `RedactMaxLength` | `2000` | 脱敏前的截断长度 |

### 5.4 配置示例

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

## 6. 监控面板

### 6.1 接入

三步，缺一不可。

```csharp
using Gadamaxi.BackendWork.Dashboard;
using Gadamaxi.BackendWork.Sql;

// ① 注册面板（在 AddBackendWork 之后）
builder.Services.AddBackendWorkDashboard(options =>
{
    options.RoutePrefix   = "/backendwork-dashboard";
    options.ReadOnly      = true;              // 生产建议只读
});

// ② 注册数据源（关系库 / MongoDB 选一个）
builder.Services.AddSqlDashboardRepository();
// 或 builder.Services.AddMongodbDashboardRepository();

var app = builder.Build();

app.UseAuthentication();     // ← 必须在 Map 之前
app.UseAuthorization();

app.MapBackendWorkDashboard();   // ③ 挂载
```

> ⚠️ 忘记第 ② 步，`MapBackendWorkDashboard()` 会抛 `InvalidOperationException`。
>
> ⚠️ 第 ③ 步必须在 `UseAuthentication()` / `UseAuthorization()` **之后**调用，否则授权策略不生效。

### 6.2 鉴权

面板本身不做登录，完全使用你应用已有的认证体系。`AuthorizationPolicy` 为 `null` 时要求用户已登录。

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

> ⚠️ **未登录访问面板会返回 404**（不是 401/403），这是认证中间件的正常行为。先登录再访问。

### 6.3 页面能看到什么

访问 `{RoutePrefix}` 即可打开面板，包含：

- **总览**：任务总数、各状态分布、节点数量
- **任务**：按状态/名称/队列筛选，查看详情与失败原因
- **并发**：当前正在执行的任务
- **周期任务**：列表、执行历史、下次运行时间
- **批量**：批量列表与各子任务状态
- **Saga**：事务列表、各步骤进度、停滞告警
- **节点**：各节点心跳、消费队列、并发占用

需要开放写操作时，面板上会出现重试、取消、立即触发、暂停、恢复等按钮。

### 6.4 HTTP API

面板同时提供一套 HTTP 接口，路径为 `{RoutePrefix}/api`，可直接对接自有运维平台。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/overview` | 总览统计 |
| GET | `/tasks` | 任务列表（支持 `state` / `name` / `queue` / 分页） |
| GET | `/tasks/{id}` | 任务详情 |
| GET | `/concurrency` | 当前并发执行情况 |
| GET | `/recurring` | 周期任务列表 |
| GET | `/recurring/{id}/history` | 周期任务执行历史 |
| GET | `/batches` | 批量列表 |
| GET | `/batches/{id}` | 批量详情 |
| GET | `/sagas` | Saga 列表（支持 `status` / `stalled`） |
| GET | `/sagas/{id}` | Saga 详情 |
| GET | `/nodes` | 节点列表 |
| GET | `/nodes/{name}` | 节点详情 |
| POST | `/tasks/{id}/retry` | 重试任务 |
| POST | `/tasks/{id}/cancel` | 取消任务 |
| POST | `/batches/{id}/retry` | 重试批量 |
| POST | `/recurring/{id}/trigger` | 立即触发 |
| POST | `/recurring/{id}/pause` | 暂停 |
| POST | `/recurring/{id}/resume` | 恢复 |

> 所有 `POST` 接口都需要开启 `EnableWriteOperations = true` 且具备 `WritePolicy` 权限。

---

## 7. 常见问题

**Q：启动时报 `Cannot find provider` / `Unable to load ADO.NET provider`**

漏装数据库驱动。按 [§2.2](#22-安装数据库驱动必做) 补上 `Microsoft.Data.Sqlite`（或 `Microsoft.Data.SqlClient` / `MySqlConnector`）。

**Q：`WorkHelper.Manager` 抛 `InvalidOperationException`**

在 `builder.Build()` 之前调用了。请改到启动完成之后（控制器、Minimal API、后台服务里），或从 `app.Services` 取 `IWorkManager`。

**Q：任务投进去了但没有执行**

依次检查：① `UseBackendServer` 是否为 `true`；② 投递的队列名是否在 `Queues` 列表里；③ 关系库是否执行过建表（`MigrateUp()`）；④ 数据库能否连通。

**Q：任务一直显示"执行中"**

默认超过 10 分钟且无节点持锁会自动重新入队。若长时间不恢复，检查节点心跳是否正常、是否有节点异常退出。可调小 `WorkProcessingTimeout` 加快恢复。

**Q：SQLite 提示 `Connection string keyword 'xxx' is not supported`**

SQLite 连接串只支持这些关键字：`Data Source`、`Mode`、`Cache`、`Password`、`Foreign Keys`、`Recursive Triggers`、`Default Timeout`、`Pooling`。其他关键字（如 `BusyTimeout`、`Journal Mode`、`DateTimeKind`）会被框架丢弃，但如果你在别处直接用原始连接串就会报错。

需要开启 WAL 时，建库阶段执行一次即可（WAL 是写在库文件里的持久属性，不能通过连接串设置）：

```csharp
using var conn = new SqliteConnection("Data Source=backendwork.db;");
conn.Open();
using var cmd = conn.CreateCommand();
cmd.CommandText = "PRAGMA journal_mode=WAL;";
cmd.ExecuteNonQuery();
```

**Q：MongoDB 报 `Transaction numbers are only allowed on a replica set member or mongos`**

单机模式不支持事务。请部署副本集（开发环境用单成员副本集即可），或接受无事务的降级模式。

**Q：Saga 一直不推进**

① 推进有 1 秒轮询间隔，稍等片刻；② 检查是否有节点异常退出但仍占着该 Saga 的锁（锁会在 1 分钟后超时）；③ 在面板 Saga 详情页查看是否有停滞标记。

**Q：面板打不开，返回 404**

未登录会被认证中间件直接返回 404。请先完成登录，再访问 `{RoutePrefix}`。

**Q：`MapBackendWorkDashboard()` 抛 `InvalidOperationException`**

忘了注册数据源。补一行 `AddSqlDashboardRepository()` 或 `AddMongodbDashboardRepository()`。
