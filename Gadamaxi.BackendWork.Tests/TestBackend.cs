using Microsoft.Data.SqlClient;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 测试后端切换：环境变量 <c>BW_TEST_BACKEND</c> = <c>sqlite</c>（默认）| <c>sqlserver</c> | <c>mongodb</c>。
    /// <para>
    /// sqlserver 模式使用 <c>BW_TEST_SQLSERVER_URI</c>（默认指向本机 LocalDB 实例 ProjectModels）。
    /// 连接串里的 Initial Catalog 只是模板，每个测试实例都会派生出独立库名，
    /// 测试结束即 DROP —— 与 SQLite 的"每实例一个临时 .db 文件"保持同一隔离口径。
    /// </para>
    /// <para>
    /// mongodb 模式需要一个可用的 mongod：连接串由 <c>BW_TEST_MONGO_URI</c> 配置，
    /// 默认指向 <c>127.0.0.1:27019</c> 的单成员副本集（<c>mongod --replSet bwtest</c>）。
    /// 首次使用时会自动探测连通性、尝试 rs.initiate 并等待 PRIMARY 就绪——
    /// 用副本集而不是 standalone 是为了保住 IWorkStorage 的多文档事务语义，
    /// 让依赖"整事务回滚"的用例在 Mongo 后端下也跑真实行为。
    /// </para>
    /// </summary>
    public static class TestBackend
    {
        public const string Sqlite = "sqlite";
        public const string SqlServer = "sqlserver";
        public const string Mongo = "mongodb";

        /// <summary>
        /// 默认 SqlServer 连接串：本机 LocalDB 实例 ProjectModels，模板库 backendwork。
        /// 实际测试库由 <see cref="ConnectionStringForDatabase"/> 按实例派生。
        /// </summary>
        public const string DefaultSqlServerConnection =
            @"Data Source=(localdb)\ProjectModels;Initial Catalog=backendwork;Integrated Security=True;Connect Timeout=30;Encrypt=True;Trust Server Certificate=False;Application Intent=ReadWrite;Multi Subnet Failover=False;Command Timeout=30";

        private static readonly string _backend =
            (Environment.GetEnvironmentVariable("BW_TEST_BACKEND") ?? Sqlite).Trim().ToLowerInvariant();

        static TestBackend()
        {
            // 读 Backend/IsMongo/IsSqlServer 任一成员都会先触发本静态构造：
            // 把"后端是否可用"的探测一次性做完，连不上时给出可操作的报错，
            // 而不是让每个用例各自抛一堆难以定位的异常。
            if (IsMongo)
            {
                EnsureMongoReady();
            }
            else if (IsSqlServer)
            {
                EnsureSqlServerReady();
            }
        }

        public static string Backend => _backend;

        public static bool IsMongo => _backend == Mongo;

        public static bool IsSqlite => _backend == Sqlite;

        /// <summary>是否为 SqlServer 后端（与 sqlite 同属 Sql 系：同样跑 FluentMigrator 迁移与 linq2db 仓储）。</summary>
        public static bool IsSqlServer => _backend == SqlServer;

        /// <summary>测试用 Mongodb 连接串。默认 directConnection 直连 27019 的单成员副本集。</summary>
        public static string MongoConnectionString =>
            Environment.GetEnvironmentVariable("BW_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27019/?directConnection=true";

        /// <summary>测试用 SqlServer 连接串（模板库，会被替换为每实例独立库）。</summary>
        public static string SqlServerConnectionString =>
            Environment.GetEnvironmentVariable("BW_TEST_SQLSERVER_URI") ?? DefaultSqlServerConnection;

        /// <summary>每个测试实例一个独立数据库（与 SQLite 模式"每实例一个临时 .db 文件"对齐）。</summary>
        public static string NewDatabaseName() => $"backendwork_test_{Guid.NewGuid():N}";

        /// <summary>
        /// 把基础连接串的 Initial Catalog 换成指定库名，其余键（数据源 / 认证 / 加密 / 超时）原样保留。
        /// </summary>
        public static string ConnectionStringForDatabase(string databaseName)
        {
            var builder = new SqlConnectionStringBuilder(SqlServerConnectionString)
            {
                InitialCatalog = databaseName
            };
            return builder.ConnectionString;
        }

        /// <summary>幂等建库：连 master 判断 DB_ID，不存在则 CREATE DATABASE。</summary>
        public static void EnsureDatabase(string databaseName)
        {
            // 库名由 Guid 生成，不含注入字符；仍按标识符转义以保持健壮
            var name = databaseName.Replace("]", "]]");

            using var connection = new SqlConnection(MasterConnectionString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"IF DB_ID(N'{databaseName}') IS NULL CREATE DATABASE [{name}]";
            command.ExecuteNonQuery();
        }

        /// <summary>删除测试库：先踢掉残留连接（后台线程可能仍持有），再 DROP。</summary>
        public static void DropDatabase(string databaseName)
        {
            var name = databaseName.Replace("]", "]]");

            using var connection = new SqlConnection(MasterConnectionString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"IF DB_ID(N'{databaseName}') IS NOT NULL BEGIN " +
                $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{name}]; END";
            command.ExecuteNonQuery();
        }

        private static string MasterConnectionString()
            => new SqlConnectionStringBuilder(SqlServerConnectionString) { InitialCatalog = "master" }.ConnectionString;

        /// <summary>
        /// 确保 SqlServer 可达（连 master 做一次 ping）。连不上直接给出可操作的报错，
        /// 而不是让每个用例各自报一堆难以定位的异常。
        /// </summary>
        private static void EnsureSqlServerReady()
        {
            try
            {
                using var connection = new SqlConnection(MasterConnectionString());
                connection.Open();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "无法连接测试 SqlServer（默认指向本机 LocalDB 实例 ProjectModels；" +
                    "可用环境变量 BW_TEST_SQLSERVER_URI 指定连接串）。", ex);
            }
        }

        /// <summary>
        /// 确保 mongod 可达且副本集就绪。等 PRIMARY 的原因：多文档事务只能在主节点上执行。
        /// 对已经初始化过的副本集 / 普通节点，replSetInitiate 的报错全部忽略（幂等）。
        /// </summary>
        private static void EnsureMongoReady()
        {
            var client = new MongoClient(MongoConnectionString);
            var admin = client.GetDatabase("admin");

            WaitUntil(() =>
                {
                    admin.RunCommand<BsonDocument>(new BsonDocument("ping", 1));
                    return true;
                },
                "无法连接测试 Mongodb（mongod 未启动？可用 BW_TEST_MONGO_URI 指定连接串）");

            // 未初始化的副本集成员上 hello 的 isWritablePrimary 为 false —— 尝试发起选举；
            // 已初始化（AlreadyInitialized）或未以 --replSet 启动（NoReplicationEnabled）的报错都视为"无需处理"
            try
            {
                var hello = admin.RunCommand<BsonDocument>(new BsonDocument("hello", 1));
                if (!hello["isWritablePrimary"].AsBoolean)
                {
                    admin.RunCommand<BsonDocument>(new BsonDocument("replSetInitiate", new BsonDocument()));
                }
            }
            catch (MongoCommandException)
            {
                // 幂等：副本集已初始化 / 根本不是副本集成员，都走这里
            }

            WaitUntil(() =>
                {
                    var hello = admin.RunCommand<BsonDocument>(new BsonDocument("hello", 1));
                    if (!hello["isWritablePrimary"].AsBoolean)
                    {
                        throw new InvalidOperationException("副本集尚未选出 PRIMARY");
                    }

                    return true;
                },
                "测试 Mongodb 副本集未就绪（60 秒内没有 PRIMARY）");
        }

        private static void WaitUntil(Func<bool> action, string error)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (action())
                    {
                        return;
                    }
                }
                catch (Exception)
                {
                    // mongod 还没起来 / 副本集还在选举：等下一轮
                }

                Thread.Sleep(500);
            }

            throw new InvalidOperationException(error);
        }
    }

    /// <summary>
    /// 标记"仅 Sql 后端可跑"的测试：Mongo 后端下在发现阶段直接跳过
    /// （用于依赖 FluentMigrator 迁移脚本或 linq2db 表结构的 Sql 专属用例；sqlite 与 sqlserver 均会执行）。
    /// </summary>
    public sealed class SqlBackendOnlyFactAttribute : FactAttribute
    {
        public SqlBackendOnlyFactAttribute()
        {
            if (TestBackend.IsMongo)
            {
                Skip = "Sql 专属：依赖 FluentMigrator 迁移 / linq2db 表结构（Mongodb 后端下无对应概念）";
            }
        }
    }
}
