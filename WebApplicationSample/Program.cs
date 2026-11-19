using System.Security.Claims;
using System.Text;
using FluentMigrator.Runner;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Dashboard;
using Gadamaxi.BackendWork.Mongodb;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Sql;
using Gadamaxi.BackendWork.Works;
using LinqToDB;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using WebApplicationSample;

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// 本示例演示「从 nuget.org 安装已发布的 0.1.0 包后如何使用」。
// 与 WebApplicationTest 的差别：
//   - 引用方式为 PackageReference（官方包），而非 ProjectReference（源码工程）
//   - 存储后端固定为 SQLite（单文件、自包含，无需 LocalDB / MongoDB 实例）
// 存储后端可通过环境变量 BW_BACKEND 切换为 mongodb / sqlserver（同 WebApplicationTest）。
// ============================================================================

var backend = (Environment.GetEnvironmentVariable("BW_BACKEND") ?? "sqlite").Trim().ToLowerInvariant();

builder.Services.AddBackendWork(new WorkServerOption
{
    ServerName = "sample-web",
    UseBackendServer = true,   // 本宿主同时参与调度 + 消费任务
    WorkerCount = 4,
    // 演示用：把定时/批量扫描间隔调短（默认 1 分钟），让延时任务和批量任务秒级可见
    SchedulePollingDelay = TimeSpan.FromSeconds(5),
    WorkPollingDelay = TimeSpan.FromSeconds(2),
}, config =>
{
    if (backend == "mongodb")
    {
        config.UseMongodbBackendWork(builder.Services, () => new MongoBackendWorkOptions(
            Environment.GetEnvironmentVariable("BW_MONGO_URI") ?? "mongodb://127.0.0.1:27017",
            Environment.GetEnvironmentVariable("BW_MONGO_DB") ?? "backendwork_sample"));
    }
    else if (backend == "sqlserver")
    {
        var dataOptions = new DataOptions()
            .UseSqlServer("Data Source=(localdb)\\ProjectModels;Initial Catalog=backendwork_sample;Integrated Security=True;Connect Timeout=30;Encrypt=True;Trust Server Certificate=False");
        config.UseSqlBackendWork(builder.Services, () => dataOptions);
    }
    else
    {
        var dataOptions = new DataOptions()
            .UseSQLite("Data Source=backendwork_sample.db;");
        config.UseSqlBackendWork(builder.Services, () => dataOptions);
    }
});

// ============================================================================
// 监控面板：读需已认证，写需 Admin 角色（演示页会签发 Cookie）
// ============================================================================
builder.Services.AddBackendWorkDashboard(options =>
{
    options.RoutePrefix = "/backendwork-dashboard";
    options.AuthorizationPolicy = "DashboardViewer";
    options.EnableWriteOperations = true;
    options.WritePolicy = "BackendWorkAdmin";
    options.ReadOnly = false;
});

// 面板仓储实现：按存储后端选择
if (backend == "mongodb")
{
    builder.Services.AddMongodbDashboardRepository();
}
else
{
    builder.Services.AddSqlDashboardRepository();
}

// ============================================================================
// 认证：Cookie（浏览器）+ JWT（API），"Smart" 按是否带 Authorization 头分流
// ============================================================================
const string JwtKey = "backendwork-sample-key-0123456789abcdef0123456789abcdef";

builder.Services
    .AddAuthentication("Smart")
    .AddPolicyScheme("Smart", "Smart", o =>
    {
        o.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.ContainsKey("Authorization") ? JwtBearerDefaults.AuthenticationScheme : "Cookie";
    })
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "backendwork-sample",
            ValidateAudience = true,
            ValidAudience = "backendwork-sample",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey))
        };
    })
    .AddCookie("Cookie", o => o.LoginPath = "/login");

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("DashboardViewer", p => p.RequireAuthenticatedUser())
    .AddPolicy("BackendWorkAdmin", p => p.RequireRole("Admin"));

builder.Services.AddRazorPages();

var app = builder.Build();

// 启动期初始化存储：
// - Sql：与生产/测试同一条 FluentMigrator 迁移路径（IMigrationRunner 为 scoped，需在 scope 内解析）
// - Mongodb：主动解析一次上下文，触发事务能力探测 + 索引幂等创建（连不上库会直接暴露）
using (var scope = app.Services.CreateScope())
{
    if (backend == "mongodb")
    {
        scope.ServiceProvider.GetRequiredService<MongoBackendWorkContext>();
    }
    else
    {
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapBackendWorkDashboard();

// 根路径跳到任务演示页
app.MapGet("/", () => Results.Redirect("/demotasks"));

// ============================================================================
// 演示端点
// ============================================================================

// 浏览器登录：/login?user=alice&role=Admin
app.MapGet("/login", async (HttpContext ctx, string? user, string? role) =>
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, string.IsNullOrWhiteSpace(user) ? "sample" : user),
        new(ClaimTypes.Role, string.IsNullOrWhiteSpace(role) ? "Admin" : role)
    };
    await ctx.SignInAsync("Cookie", new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookie")));
    return Results.Redirect("/backendwork-dashboard");
});

// API 令牌：POST /token
app.MapPost("/token", (SampleLoginRequest req) =>
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, string.IsNullOrWhiteSpace(req.User) ? "sample" : req.User),
        new(ClaimTypes.Role, string.IsNullOrWhiteSpace(req.Role) ? "Admin" : req.Role)
    };
    var credentials = new SigningCredentials(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)), SecurityAlgorithms.HmacSha256);
    var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
        issuer: "backendwork-sample",
        audience: "backendwork-sample",
        claims: claims,
        expires: DateTime.UtcNow.AddHours(8),
        signingCredentials: credentials);
    return Results.Ok(new
    {
        data = new { token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token) }
    });
});

// 即时任务
app.MapPost("/demo/enqueue", async () =>
{
    var workId = await WorkHelper.Manager.Enqueue(
        () => SampleJobs.Hello("hello from sample"), "sample.hello");
    return Results.Ok(new { data = new { workId } });
});

// 延时任务
app.MapPost("/demo/schedule", async (int? seconds) =>
{
    var delay = TimeSpan.FromSeconds(Math.Max(1, seconds ?? 10));
    var workId = await WorkHelper.Manager.Schedule(
        () => SampleJobs.Hello("delayed hello from sample"), delay, "sample.schedule");
    return Results.Ok(new { data = new { workId, delaySeconds = delay.TotalSeconds } });
});

// 批量任务
app.MapPost("/demo/batch", async (int? count) =>
{
    var total = Math.Clamp(count ?? 10, 1, 200);
    var batchId = await WorkHelper.Batch.Create(ctx =>
    {
        for (var i = 1; i <= total; i++)
        {
            var message = $"sample-batch#{i}";
            ctx.Enqueue(() => SampleJobs.BatchItem(message));
        }
    }, "sample.batch");
    return Results.Ok(new { data = new { batchId, count = total } });
});

// 周期任务（每分钟）
app.MapPost("/demo/recurring", async () =>
{
    await WorkHelper.Recurring.AddOrUpdate<SampleRecurringJob>(
        "sample-recurring",
        job => job.Tick($"tick {DateTime.UtcNow}"),
        "* * * * *",
        "default",
        new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Local },
        "sample.recurring");
    return Results.Ok(new { data = new { id = "sample-recurring" } });
});

// 任务执行日志（内存，演示用）
app.MapGet("/demo/log", () => Results.Ok(new { data = SampleJobs.Log.ToArray() }));

app.Run();

public record SampleLoginRequest(string? User, string? Role);
