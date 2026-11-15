using FluentMigrator.Runner;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Mongodb;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Sql;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works;
using LinqToDB;
using LinqToDB.DataProvider.SQLite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 测试基础设施：每个测试实例使用独立的临时库（SQLite 文件或 Mongodb 数据库，由
    /// <see cref="TestBackend"/> 按环境变量 BW_TEST_BACKEND 决定）。
    /// Sql 建库走生产同一条迁移路径（FluentMigrator 的 IMigrationRunner.MigrateUp），
    /// Mongodb 建索引由 MongoBackendWorkContext 在首次解析时幂等创建——
    /// 两者都保证测试库结构与生产 schema 始终一致。
    /// </summary>
    public abstract class BackendWorkTestBase : IDisposable
    {
        protected ServiceProvider Provider { get; }
        protected WorkServerOption Option { get; }
        protected IWorkManager Manager { get; }
        protected IWorkQuery Query { get; }
        protected IWorkStorage Storage { get; }
        protected IWorkQueue Queue { get; }
        protected IDistributedLock Lock { get; }

        /// <summary>后端无关的直查库/种库入口（Sql 用 linq2db、Mongodb 用驱动实现）。</summary>
        protected ITestDataAccess Db { get; }

        private readonly string _dbFile = string.Empty;
        private readonly string? _mongoDatabaseName;
        private readonly string? _sqlServerDatabaseName;
        private readonly bool _deleteDatabaseOnDispose;

        /// <param name="processingTimeout">卡死判定阈值，测试卡死恢复时调小</param>
        /// <param name="deleteDatabaseOnDispose">
        /// 是否在释放时删除临时库。端到端测试启动的是真实后台线程（进程无法被真正停止），
        /// 保留库可避免残留线程反复重建已删除的库。
        /// </param>
        protected BackendWorkTestBase(TimeSpan? processingTimeout = null, bool deleteDatabaseOnDispose = true)
        {
            _deleteDatabaseOnDispose = deleteDatabaseOnDispose;

            Option = new WorkServerOption();
            if (processingTimeout != null)
            {
                Option.WorkProcessingTimeout = processingTimeout.Value;
            }

            var services = new ServiceCollection();
            services.AddLogging();
            // 排查开关：设置 BW_TEST_LOG_FILE 时把框架日志写入该文件（不影响常规运行）
            var testLogFile = Environment.GetEnvironmentVariable("BW_TEST_LOG_FILE");
            if (!string.IsNullOrEmpty(testLogFile))
            {
                services.AddLogging(b => b.AddProvider(new FileLoggerProvider(testLogFile)));
            }

            if (TestBackend.IsMongo)
            {
                // 每实例独立数据库 + 自动就绪探测（副本集 initiate / 等 PRIMARY）
                _mongoDatabaseName = TestBackend.NewDatabaseName();
                var mongoOptions = new MongoBackendWorkOptions(TestBackend.MongoConnectionString, _mongoDatabaseName);

                services.AddBackendWork(Option, config =>
                {
                    config.UseMongodbBackendWork(services, () => mongoOptions);
                });

                Provider = services.BuildServiceProvider();
                // 生产路径中由 IConfig 单例初始化静态服务定位器
                Provider.GetRequiredService<IConfig>();
                // 显式解析一次：触发事务探测与幂等建索引（连不上库直接失败）
                Db = new MongoTestDataAccess(Provider.GetRequiredService<MongoBackendWorkContext>());
            }
            else if (TestBackend.IsSqlServer)
            {
                // 每实例独立数据库（与 SQLite 的临时文件、Mongo 的临时库同口径）：
                // 库名在基础连接串上派生，LocalDB 上 CREATE DATABASE 即建库，Dispose 时 DROP。
                _sqlServerDatabaseName = TestBackend.NewDatabaseName();
                TestBackend.EnsureDatabase(_sqlServerDatabaseName);

                var dataOptions = new DataOptions()
                    .UseSqlServer(TestBackend.ConnectionStringForDatabase(_sqlServerDatabaseName));

                services.AddBackendWork(Option, config =>
                {
                    config.UseSqlBackendWork(services, () => dataOptions);
                });

                Provider = services.BuildServiceProvider();
                Provider.GetRequiredService<IConfig>();

                // 与生产完全一致：交给 FluentMigrator 迁移脚本建库（SqlServer runner）
                Provider.GetRequiredService<IMigrationRunner>().MigrateUp();

                Db = new SqlTestDataAccess(Provider.GetRequiredService<DataOptions>());
            }
            else
            {
                _dbFile = Path.Combine(Path.GetTempPath(), $"backendwork_test_{Guid.NewGuid():N}.db");
                // 端到端测试会启动真实的十余个后台工作线程并发读写同一个库，因此需要
                // "遇锁等待" + "WAL"两项并发口径。linq2db 6.5 起 SQLite 由 Microsoft.Data.Sqlite 承载，
                // 其连接串只认自己的关键字子集，原 BusyTimeout / Journal Mode / Synchronous 会直接抛
                // "Connection string keyword 'busytimeout' is not supported"，故按下述等价方式迁移：
                // - BusyTimeout(毫秒) → Default Timeout(秒)：语义同为"在锁上等待而不是立即失败"；
                // - Journal Mode=WAL 是写在库文件头里的持久属性，改为建库前用 PRAGMA 固化一次
                //   （EnableSqliteWal），连接串里无法表达。
                EnableSqliteWal(_dbFile);
                var dataOptions = new DataOptions()
                    .UseSQLite($"Data Source={_dbFile};Default Timeout=60", SQLiteProvider.Microsoft);

                services.AddBackendWork(Option, config =>
                {
                    config.UseSqlBackendWork(services, () => dataOptions);
                });

                Provider = services.BuildServiceProvider();
                Provider.GetRequiredService<IConfig>();

                // 与生产完全一致：交给 FluentMigrator 迁移脚本建库
                Provider.GetRequiredService<IMigrationRunner>().MigrateUp();

                Db = new SqlTestDataAccess(Provider.GetRequiredService<DataOptions>());
            }

            Manager = Provider.GetRequiredService<IWorkManager>();
            Query = Provider.GetRequiredService<IWorkQuery>();
            Storage = Provider.GetRequiredService<IWorkStorage>();
            Queue = Provider.GetRequiredService<IWorkQueue>();
            Lock = Provider.GetRequiredService<IDistributedLock>();
        }

        protected ILogger<T> CreateLogger<T>() => Provider.GetRequiredService<ILoggerFactory>().CreateLogger<T>();

        /// <summary>
        /// 打开一个指向当前测试库的连接（仅 Sql 后端；Mongodb 模式请使用 <see cref="Db"/>）。
        /// 供 Sql 专属测试（迁移验证、Dashboard 仓储）使用。
        /// </summary>
        protected DBConnection OpenDb() => new DBConnection(Provider.GetRequiredService<DataOptions>());

        /// <summary>
        /// 把 SQLite 库切成 WAL 日志模式：多线程并发读写时读不阻塞写、写不阻塞读，
        /// 显著降低 "database is locked"（这也是原先连接串里 Journal Mode=WAL 的用途）。
        /// WAL 是记录在库文件头中的持久属性，只需在任何普通连接建立之前设置一次即可全局生效；
        /// 这里刻意用 Pooling=False 的短连接设置后真正关闭，避免把旧日志模式的连接留在连接池里。
        /// </summary>
        private static void EnableSqliteWal(string dbFile)
        {
            using var connection = new SqliteConnection($"Data Source={dbFile};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            command.ExecuteScalar();
        }

        /// <summary>构造一个最小可用的执行上下文</summary>
        protected WorkProcessContext NewProcessContext()
        {
            var server = new ServerContext(CancellationToken.None, null!, new List<IWorkProcess>().AsReadOnly())
            {
                ServerName = "test-server",
                Queues = new List<string> { Helper.DefaultQueue },
            };
            return new WorkProcessContext(server);
        }

        protected Task<string> EnqueueSimpleWork()
            => Manager.Enqueue(() => TestJobs.StaticWork(), "simple-work", Helper.DefaultQueue);

        protected Task<string> EnqueueFailWork()
            => Manager.Enqueue(() => TestJobs.FailingWork(), "fail-work", Helper.DefaultQueue);

        /// <summary>读取 Work 表里持久化的任务名（直接查库，验证落库结果）</summary>
        protected Task<string?> WorkNameInDb(string workId) => Db.GetWorkNameAsync(workId);

        /// <summary>读取 Batch 表里持久化的批量名</summary>
        protected Task<string?> BatchNameInDb(string batchId) => Db.GetBatchNameAsync(batchId);

        /// <summary>按加入顺序读取批量子任务名</summary>
        protected Task<List<string>> BatchItemNamesInDb(string batchId) => Db.GetBatchItemNamesAsync(batchId);

        protected Task<bool> QueueRowExists(string workId) => Db.QueueRowExistsAsync(workId);

        /// <summary>读取队列消息所在的队列名（验证队列路由）</summary>
        protected Task<string?> QueueNameOf(string workId) => Db.GetQueueNameAsync(workId);

        protected Task<bool> WorkRowExists(string workId) => Db.WorkExistsAsync(workId);

        /// <summary>删除指定工作的队列消息，模拟消息已被消费出队</summary>
        protected Task RemoveQueueRow(string workId) => Db.DeleteQueueRowAsync(workId);

        /// <summary>把队列消息的可见性超时起点改到很久以前，模拟消息取走后处理节点崩溃</summary>
        protected Task AgeQueueRow(string workId, TimeSpan age) => Db.AgeQueueRowAsync(workId, age);

        /// <summary>
        /// 轮询等待条件成立（真实后台调度是异步的，测试需要等待其推进）。
        /// 返回条件是否在超时前成立。
        /// </summary>
        protected async Task<bool> WaitUntil(Func<Task<bool>> condition,
            TimeSpan? timeout = null,
            int pollIntervalMilliseconds = 100)
        {
            var limit = timeout ?? TimeSpan.FromSeconds(30);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < limit)
            {
                if (await condition())
                {
                    return true;
                }
                await Task.Delay(pollIntervalMilliseconds);
            }

            return await condition();
        }

        public void Dispose()
        {
            Provider.Dispose();

            if (!_deleteDatabaseOnDispose)
            {
                return;
            }

            if (_mongoDatabaseName != null)
            {
                try { new MongoClient(TestBackend.MongoConnectionString).DropDatabase(_mongoDatabaseName); }
                catch { /* mongod 可能已被停止，忽略 */ }
            }
            else if (_sqlServerDatabaseName != null)
            {
                try { TestBackend.DropDatabase(_sqlServerDatabaseName); }
                catch { /* 库可能已被删除 / 实例已停，忽略 */ }
            }
            else
            {
                try { File.Delete(_dbFile); } catch { /* 文件可能仍被占用，忽略 */ }
            }
        }
    }

    /// <summary>
    /// 供表达式树任务定义使用的测试作业类（静态方法，无需实例化）。
    /// 静态方法覆盖 Expression&lt;Action&gt; / Expression&lt;Func&lt;Task&gt;&gt; 两种重载，
    /// 实例方法（<see cref="InstanceJobs"/>）覆盖带泛型参数的两种重载。
    /// </summary>
    public static class TestJobs
    {
        public static int InvokeCount;

        /// <summary>按执行顺序记录的任务标记，用于验证执行次数与先后顺序（如连续任务）</summary>
        public static readonly ConcurrentQueue<string> Records = new ConcurrentQueue<string>();

        private static int _flakyInvoke;

        public static void Reset()
        {
            Interlocked.Exchange(ref InvokeCount, 0);
            Interlocked.Exchange(ref _flakyInvoke, 0);
            while (Records.TryDequeue(out _)) { }
        }

        /// <summary>
        /// 前 <paramref name="failTimes"/> 次调用抛异常、之后成功的作业。
        /// 每次调用都会记录标记（含失败的那次），便于断言"失败子任务被重跑、成功子任务没有"。
        /// </summary>
        public static void Flaky(string marker, int failTimes)
        {
            Interlocked.Increment(ref InvokeCount);
            var idx = Interlocked.Increment(ref _flakyInvoke);
            Records.Enqueue(marker);

            if (idx <= failTimes)
            {
                throw new InvalidOperationException($"flaky failure {idx}/{failTimes}");
            }
        }

        public static Task StaticWork()
        {
            Interlocked.Increment(ref InvokeCount);
            return Task.CompletedTask;
        }

        public static Task FailingWork()
        {
            Interlocked.Increment(ref InvokeCount);
            throw new InvalidOperationException("business failure for test");
        }

        /// <summary>可被 Expression&lt;Func&lt;Task&gt;&gt; 引用的异步静态方法</summary>
        public static Task StaticWorkAsync()
        {
            Interlocked.Increment(ref InvokeCount);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 占用指定执行时间的作业：用于验证优雅停机会等待在途任务（#9）。
        /// 标记在执行结束前才记录，因此"标记出现"即代表任务真的跑完了。
        /// </summary>
        public static void SlowWork(string marker, int delayMilliseconds)
        {
            Interlocked.Increment(ref InvokeCount);
            Thread.Sleep(delayMilliseconds);
            Records.Enqueue(marker);
        }

        /// <summary>记录一个执行标记（同步重载）</summary>
        public static void Mark(string marker)
        {
            Interlocked.Increment(ref InvokeCount);
            Records.Enqueue(marker);
        }

        /// <summary>记录一个执行标记（异步重载）</summary>
        public static Task MarkAsync(string marker)
        {
            Interlocked.Increment(ref InvokeCount);
            Records.Enqueue(marker);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 实例方法作业：验证 Workload 能正确序列化实例类型、方法名与参数，
    /// 且执行期由 WorkActivator 创建实例后调用。
    /// </summary>
    public class InstanceJobs
    {
        public void Write(string message)
        {
            TestJobs.Mark(message);
        }

        public Task WriteAsync(string message)
        {
            return TestJobs.MarkAsync(message);
        }
    }
}
