using FluentMigrator.Runner;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Sql;
using Gadamaxi.BackendWork.Works;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading.Tasks;

namespace RecurringTest
{
    /// <summary>
    /// 周期任务可靠性验证工具。
    /// 用法：
    ///   unit               — 无 DB 的窗口语义模拟（跨分钟/宕机恢复语义推演）
    ///   setup &lt;n&gt;         — 注册 1 个 latency 任务 + n 个 stress 任务（每分钟），不启动执行服务
    ///   run &lt;seconds&gt;     — 启动执行服务运行指定秒数后优雅退出
    ///   crash-test &lt;mode&gt; — crash-test setup / crash-test run（持久 DB，用于强杀重启）
    /// </summary>
    internal class Program
    {
        private static readonly string BaseDir = AppContext.BaseDirectory;
        private static readonly string DbFile = Path.Combine(BaseDir, "recurring-test.db");
        private static readonly string ExecLog = Path.Combine(BaseDir, "exec.log");
        private static readonly object LogLock = new object();

        static async Task<int> Main(string[] args)
        {
            var mode = args.Length > 0 ? args[0] : "help";
            switch (mode)
            {
                case "unit":
                    return UnitWindowSimulation();
                case "probe":
                    return Probe.Run();
                case "setup":
                    await SetupAsync(ParseArg(args, 1, 1), freshDb: true);
                    return 0;
                case "run":
                    return await RunAsync(ParseArg(args, 1, 240));
                case "crash-test":
                    return await CrashTestAsync(args.Length > 1 ? args[1] : "setup");
                default:
                    Console.WriteLine("usage: unit | setup <n> | run <seconds> | crash-test <setup|run>");
                    return 1;
            }
        }

        private static int ParseArg(string[] args, int index, int fallback)
            => args.Length > index && int.TryParse(args[index], out var v) ? v : fallback;

        private static IHost BuildHost(bool startServer)
        {
            var dataOptions = new DataOptions()
                .UseSQLite($"Data Source={DbFile};Cache=Shared");

            var host = new HostBuilder()
                .ConfigureLogging(x => x.AddConsole().SetMinimumLevel(LogLevel.Warning))
                .ConfigureServices(services =>
                {
                    services.AddBackendWork(new WorkServerOption
                    {
                        ServerName = "recurring-test-1",
                        UseBackendServer = startServer,
                        WorkerCount = 8
                    }, config =>
                    {
                        config.UseSqlBackendWork(services, () => dataOptions);
                    });
                })
                .Build();

            // 建库/迁移
            using (var scope = host.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
            }
            return host;
        }

        // ------------------------------------------------------------------
        // setup：注册周期任务（不启动执行服务）
        // ------------------------------------------------------------------
        private static async Task SetupAsync(int stressCount, bool freshDb)
        {
            if (freshDb && File.Exists(DbFile))
            {
                File.Delete(DbFile);
            }
            if (File.Exists(ExecLog))
            {
                File.Delete(ExecLog);
            }

            using var host = BuildHost(startServer: false);
            await host.StartAsync();
            DependencyInjectionHelper.Set(host.Services); // WorkHelper 静态入口依赖全局 provider

            // 1 个精度观测任务
            await WorkHelper.Recurring.AddOrUpdate<RecurringTestJob>(
                "latency-001", j => j.Run("latency-001"),
                "* * * * *", "default",
                new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Local },
                "latency-001");

            // stress 压力任务
            for (var i = 0; i < stressCount; i++)
            {
                var name = $"stress-{i:D3}";
                await WorkHelper.Recurring.AddOrUpdate<RecurringTestJob>(
                    name, j => j.Run(name),
                    "* * * * *", "default",
                    new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Local },
                    name);
            }

            Console.WriteLine($"SETUP-DONE stress={stressCount} db={DbFile}");
            await host.StopAsync();
        }

        // ------------------------------------------------------------------
        // run：启动执行服务，运行指定秒数
        // ------------------------------------------------------------------
        private static async Task<int> RunAsync(int seconds)
        {
            using var host = BuildHost(startServer: true);
            await host.StartAsync();
            Console.WriteLine($"RUN-START utc={DateTime.UtcNow:HH:mm:ss.fff} for {seconds}s");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            Console.WriteLine($"RUN-END utc={DateTime.UtcNow:HH:mm:ss.fff}");
            await host.StopAsync();
            return 0;
        }

        // ------------------------------------------------------------------
        // crash-test：复用持久 DB（不删除），setup 后可强杀再 run 验证恢复
        // ------------------------------------------------------------------
        private static async Task<int> CrashTestAsync(string sub)
        {
            if (sub == "setup")
            {
                if (File.Exists(DbFile)) File.Delete(DbFile);
                if (File.Exists(ExecLog)) File.Delete(ExecLog);
                using var host = BuildHost(startServer: false);
                await host.StartAsync();
                DependencyInjectionHelper.Set(host.Services);

                // 常规每分钟任务 + 一个“每小时整点”任务用于验证窗口语义
                await WorkHelper.Recurring.AddOrUpdate<RecurringTestJob>(
                    "crash-minute", j => j.Run("crash-minute"),
                    "* * * * *", "default",
                    new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Local },
                    "crash-minute");
                await WorkHelper.Recurring.AddOrUpdate<RecurringTestJob>(
                    "crash-stress-000", j => j.Run("crash-stress-000"),
                    "* * * * *", "default",
                    new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Local },
                    "crash-stress-000");

                Console.WriteLine("CRASH-SETUP-DONE");
                await host.StopAsync();
                return 0;
            }

            return await RunAsync(ParseArg(new[] { "run", sub }, 1, 120));
        }

        // ------------------------------------------------------------------
        // unit：无 DB 的窗口语义模拟
        // ------------------------------------------------------------------
        private static int UnitWindowSimulation()
        {
            Console.WriteLine("=== 窗口语义模拟（无 DB，直接调用 RecurringWork） ===");

            // 场景 A：每小时任务，计划 10:00 触发，但进程在 09:59 抢占缓存后崩溃，
            // 重启后 10:01:30 才重新扫描到。是否补触发？
            var hourly = new RecurringWork("sim-hourly")
            {
                Cron = "0 * * * *",
                TimeZone = TimeZoneInfo.Local.Id,
                CreateUtcTime = new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc)
            };
            var lastExec = new DateTime(2026, 9, 22, 9, 0, 0, DateTimeKind.Utc);
            var scanTime = new DateTime(2026, 9, 22, 10, 1, 30, DateTimeKind.Utc);

            var next = hourly.GenNextExecuteTime(lastExec, scanTime);
            var hit = hourly.CheckCurrentTimeRun(scanTime.UnixMillisecondStamp());
            Console.WriteLine($"[每小时任务] LastExecute={lastExec:HH:mm} 扫描时刻={scanTime:HH:mm:ss}");
            Console.WriteLine($"  NextExecuteUtcTime={hourly.NextExecuteUtcTime:yyyy-MM-dd HH:mm}");
            Console.WriteLine($"  CheckCurrentTimeRun(扫描时刻)={hit}  → 10:00 错过的执行{(hit ? "会补触发" : "不会补触发（静默跳到下一窗口）")}");

            // 场景 B：每分钟任务，同样的崩溃时序
            var minutely = new RecurringWork("sim-minutely")
            {
                Cron = "* * * * *",
                TimeZone = TimeZoneInfo.Local.Id,
                CreateUtcTime = new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc)
            };
            var lastExec2 = new DateTime(2026, 9, 22, 9, 59, 0, DateTimeKind.Utc);
            minutely.GenNextExecuteTime(lastExec2, scanTime);
            var hit2 = minutely.CheckCurrentTimeRun(scanTime.UnixMillisecondStamp());
            Console.WriteLine($"[每分钟任务] 扫描时刻={scanTime:HH:mm:ss} CheckCurrentTimeRun={hit2} → {(hit2 ? "下一分钟自然续跑" : "异常")}");

            return 0;
        }
    }

    /// <summary>测试任务负载：把执行时刻精确写入日志文件。</summary>
    public class RecurringTestJob
    {
        public string JobName { get; set; } = string.Empty;

        private static readonly object LogLock = new object();

        public Task Run(string name)
        {
            var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}|{name}";
            lock (LogLock)
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "exec.log"), line + Environment.NewLine);
            }
            return Task.CompletedTask;
        }
    }
}
