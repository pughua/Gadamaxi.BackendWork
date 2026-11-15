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

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// 1. 后台任务框架 + 监控面板
//    存储后端由环境变量 BW_BACKEND 切换：
//    - sqlite（默认）：单文件库 backendwork.db（生产换 UseSqlServer/UseMySql 连接串即可）
//    - mongodb：连接串 BW_MONGO_URI（默认 mongodb://127.0.0.1:27017）、库名 BW_MONGO_DB
//      （默认 backendwork_demo）。多文档事务需要副本集；单机退化时启动日志会记警告。
//    - sqlserver：sqlserver数据库
//    - UseBackendServer=true：本宿主同时消费任务 + 参与调度
// ============================================================================

var backend = (Environment.GetEnvironmentVariable("BW_BACKEND") ?? "sqlserver").Trim().ToLowerInvariant();

// 演示/巡检调参（可选）：BW_WORK_RETRY_COUNT 失败重试次数、BW_WORK_RETRY_DELAY_SECONDS 重试间隔秒数。
// 不设置时使用框架默认（5 次 / 1 分钟）；做端到端巡检时设成 1 次 / 2 秒，可让失败任务秒级收敛到终态。
var retryCount = int.TryParse(Environment.GetEnvironmentVariable("BW_WORK_RETRY_COUNT"), out var rc) ? rc : 0;
var retryDelaySeconds = int.TryParse(Environment.GetEnvironmentVariable("BW_WORK_RETRY_DELAY_SECONDS"), out var rd) ? rd : 0;

builder.Services.AddBackendWork(new WorkServerOption
{
    ServerName = backend == "mongodb" ? "web-demo-mongo" : "web-demo-1",
    UseBackendServer = true,
    // 演示宿主的消费并发度：调大以便面板「并发执行」视图能同时看到多个运行中任务
    WorkerCount = 8,
    WorkRetryCount = retryCount > 0 ? retryCount : Helper.DefaultWorkRetryCount,
    WorkRetryDelay = retryDelaySeconds > 0 ? TimeSpan.FromSeconds(retryDelaySeconds) : TimeSpan.FromMinutes(1)
}, config =>
{   
    if (backend == "mongodb")
    {
        config.UseMongodbBackendWork(builder.Services, () => new MongoBackendWorkOptions(
            Environment.GetEnvironmentVariable("BW_MONGO_URI") ?? "mongodb://127.0.0.1:27017",
            Environment.GetEnvironmentVariable("BW_MONGO_DB") ?? "backendwork_demo"));
    }
    else if(backend== "sqlserver")
    {
        var dataOptions = new DataOptions()
            .UseSqlServer("Data Source=(localdb)\\ProjectModels;Initial Catalog=backendwork;Integrated Security=True;Connect Timeout=30;Encrypt=True;Trust Server Certificate=False;Application Intent=ReadWrite;Multi Subnet Failover=False;Command Timeout=30");
        config.UseSqlBackendWork(builder.Services, () => dataOptions);
    }
    else
    {
        var dataOptions = new DataOptions()
            .UseSQLite("Data Source=backendwork.db;");
        config.UseSqlBackendWork(builder.Services, () => dataOptions);
    }
});

// ============================================================================
// 2. 监控面板（两种后端都启用：Sql 用 linq2db 仓储，Mongodb 用 Mongo 仓储）
//    读策略要求已认证（JWT / Cookie 均可），写操作需要 Admin 角色
// ============================================================================

builder.Services.AddBackendWorkDashboard(options =>
{
    options.RoutePrefix = "/backendwork-dashboard";
    options.AuthorizationPolicy = "DashboardViewer";
    options.EnableWriteOperations = true;
    options.WritePolicy = "BackendWorkAdmin";
    options.ReadOnly = false; // 演示打开写操作；生产建议 true + 按需放开
});

// 面板仓储实现：按存储后端选择（换其他数据库时替换为对应的 IDashboardRepository 实现）
if (backend == "mongodb")
{
    builder.Services.AddMongodbDashboardRepository();
}
else
{
    builder.Services.AddSqlDashboardRepository();
}

// ============================================================================
// 2. 认证：JWT + Cookie 双方案（演示）
//    - 策略方案 "Smart"：请求带 Authorization: Bearer 头走 JWT（API 客户端），
//      否则走 Cookie（浏览器）。面板自身不做认证，完全跟随宿主。
// ============================================================================

const string JwtKey = "backendwork-demo-key-0123456789abcdef0123456789abcdef";

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
            ValidIssuer = "backendwork-demo",
            ValidateAudience = true,
            ValidAudience = "backendwork-demo",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey))
        };
    })
    .AddCookie("Cookie", o => o.LoginPath = "/login-page");

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("DashboardViewer", p => p.RequireAuthenticatedUser())
    .AddPolicy("BackendWorkAdmin", p => p.RequireRole("Admin"));

builder.Services.AddControllers();
builder.Services.AddRazorPages(); // 演示页 /demotasks：创建任务/延时任务/周期任务/saga 事务测试
builder.Services.AddOpenApi();

var app = builder.Build();

// 启动期初始化存储：
// - Sql：与生产/测试同一条 FluentMigrator 迁移路径（IMigrationRunner 为 scoped，需在 scope 内解析）
// - Mongodb：主动解析一次上下文，触发事务能力探测 + 索引幂等创建（连不上库会直接暴露）
using (var scope = app.Services.CreateScope())
{
    if (backend == "mongodb")
    {
        scope.ServiceProvider.GetRequiredService<MongoBackendWorkContext>();
        Console.WriteLine($"[BackendWork] Mongodb 后端就绪（{builder.Configuration["BW_MONGO_DB"] ?? "backendwork_demo"}），事务支持见启动日志警告");
    }
    else
    {
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();

// 根路径跳到任务演示页
app.MapGet("/", () => Results.Redirect("/demotasks"));

// ============================================================================
// 3. 演示端点：签发 Cookie / JWT，便于登录后打开面板
// ============================================================================

// 浏览器登录：/login?user=alice&role=Admin
app.MapGet("/login", async (HttpContext ctx, string? user, string? role) =>
{
    var name = string.IsNullOrWhiteSpace(user) ? "alice" : user;
    var claims = new List<Claim> { new(ClaimTypes.Name, name) };
    claims.Add(new Claim(ClaimTypes.Role, string.IsNullOrWhiteSpace(role) ? "Admin" : role));
    var identity = new ClaimsIdentity(claims, "Cookie");
    await ctx.SignInAsync("Cookie", new ClaimsPrincipal(identity));
    return Results.Redirect("/backendwork-dashboard");
});

// API 登录：POST /token，body { "user": "bob", "role": "Admin" }，返回 JWT
app.MapPost("/token", (DemoLoginRequest req) =>
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, string.IsNullOrWhiteSpace(req.User) ? "bob" : req.User),
        new(ClaimTypes.Role, string.IsNullOrWhiteSpace(req.Role) ? "Admin" : req.Role)
    };
    var credentials = new SigningCredentials(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)), SecurityAlgorithms.HmacSha256);
    var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
        issuer: "backendwork-demo",
        audience: "backendwork-demo",
        claims: claims,
        expires: DateTime.UtcNow.AddHours(8),
        signingCredentials: credentials);
    return Results.Ok(new
    {
        data = new { token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token) }
    });
});

// 演示任务：入队一个示例后台任务 + 注册一个每分钟周期任务，便于面板有数据可看
app.MapPost("/demo/enqueue", async () =>
{
    var workId = await WorkHelper.Manager.Enqueue(() => Console.WriteLine($"demo hello {DateTime.UtcNow}"), "demo.hello");
    return Results.Ok(new { data = new { workId } });
});

// 演示周期任务：注册一个每分钟的示例周期任务
app.MapPost("/demo/recurring", async () =>
{
    await WorkHelper.Recurring.AddOrUpdate<DemoRecurringJob>(
        "demo-recurring",
        job => job.Run($"recurring tick {DateTime.UtcNow}"),
        "* * * * *",
        "default",
        new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Local },
        "demo.recurring");
    return Results.Ok(new { data = new { id = "demo-recurring" } });
});

app.MapBackendWorkDashboard();

app.Run();

public record DemoLoginRequest(string? User, string? Role);

public class DemoRecurringJob
{
    public Task Run(string message)
    {
        Console.WriteLine(message);
        return Task.CompletedTask;
    }
}
