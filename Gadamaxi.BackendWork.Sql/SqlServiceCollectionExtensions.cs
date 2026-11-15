using FluentMigrator.Runner;
using Gadamaxi.BackendWork.Server;
using LinqToDB;
using LinqToDB.Mapping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Gadamaxi.BackendWork.Sql
{
    public static class SqlServiceCollectionExtensions
    {
        public static IConfig UseSqlBackendWork(this IConfig config, IServiceCollection services,Func<DataOptions> setOption)
        {
            var options = setOption();

            // SQLite 连接串口径收敛：linq2db 6.5 起 ProviderName.SQLite 由 Microsoft.Data.Sqlite 承载
            // （linq2db.SQLite 包已退化为纯 T4 模板、不再携带任何 ADO.NET provider），
            // 连接串必须只用 Microsoft.Data.Sqlite 认识的关键字，详见 ToSqliteConnectionString。
            // 收敛结果同时用于 linq2db 运行期与下面的 FluentMigrator 建表，避免两处口径漂移。
            using (var probe = new DBConnection(options))
            {
                if (probe.DataProvider != null
                    && probe.DataProvider.Name.StartsWith(ProviderName.SQLite, StringComparison.OrdinalIgnoreCase))
                {
                    options = options.UseConnectionString(ToSqliteConnectionString(probe.ConnectionString));
                }
            }

            services.AddTransient((p) =>
            {
                return options;
            });

            config.WorkStorageFunc = p =>
            {
                var options = p.GetService<DataOptions>();
                var storage = new WorkStorage(options);
                return storage;
            };

            config.WorkQueryFunc = p => 
            {
                var options = p.GetService<DataOptions>();
                var query = new WorkQuery(options);
                return query;
            };

            config.WorkQueueFunc = p =>
            {
                var options = p.GetService<DataOptions>();
                // 每轮出队上限来自 WorkServerOption（#22）
                var serverOption = p.GetService<WorkServerOption>() ?? new WorkServerOption();
                var queue = new WorkQueue(options, serverOption);
                return queue;
            };

            config.DistributedLockFunc = p =>
            {
                var fact = p.GetService<ILoggerFactory>()??throw new ArgumentNullException(nameof(ILoggerFactory));
                var loc = new DistributedLock(fact);
                return loc;
            };

            var db = new DBConnection(options);
            var isSqlite = db.DataProvider.Name.StartsWith(ProviderName.SQLite);

            services.AddFluentMigratorCore()
                .ConfigureRunner(rb =>
                {
                    if(isSqlite)
                    {
                        rb.AddSQLite();
                    }
                    else if(db.DataProvider.Name.StartsWith(ProviderName.SqlServer))
                    {
                        rb.AddSqlServer();
                    }

                    rb.WithGlobalConnectionString(db.ConnectionString);
                    rb.ScanIn(typeof(DBConnection).Assembly).For.Migrations();
                });

            return config;
        }

        /// <summary>
        /// Microsoft.Data.Sqlite 认识的连接串关键字（全量白名单，其余一律丢弃）。
        /// </summary>
        private static readonly HashSet<string> SqliteKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "data source", "datasource", "filename", "mode", "cache", "password",
            "foreign keys", "recursive triggers", "default timeout", "defaulttimeout", "pooling",
        };

        /// <summary>
        /// 把 SQLite 连接串收敛成 Microsoft.Data.Sqlite 认识的子集，未识别的关键字一律丢弃。
        /// <para>
        /// 背景：linq2db 6.5 起 <see cref="ProviderName.SQLite"/> 的 ADO.NET 实现是 Microsoft.Data.Sqlite，
        /// 而它只接受上面 <see cref="SqliteKeywords"/> 里的关键字；传入 BusyTimeout / Journal Mode /
        /// Synchronous / DateTimeKind 等关键字会在**建连接时**直接抛
        /// "Connection string keyword 'busytimeout' is not supported"，linq2db 运行期与 FluentMigrator 建表都跑不起来。
        /// </para>
        /// <para>
        /// 两个需要留意的语义映射（都是从旧 System.Data.SQLite 时代沿用下来的写法）：
        /// <list type="bullet">
        /// <item>BusyTimeout(毫秒) → Default Timeout(秒)：语义同为"在锁上等待而不是立即失败"。</item>
        /// <item>Journal Mode=WAL：Microsoft.Data.Sqlite 无法在连接串里表达。WAL 是写在库文件头里的
        /// 持久属性，正确做法是建库时用一条 <c>PRAGMA journal_mode=WAL</c> 固化一次
        /// （测试基类 BackendWorkTestBase.EnableSqliteWal 即是此做法），而不是每个连接都带一遍。</item>
        /// <item>DateTimeKind=Utc：仅 System.Data.SQLite（<see cref="ProviderName.SQLiteClassic"/>）需要，
        /// 用于避免它把 ISO8601 UTC 文本按本地挂钟解析而整体偏移一个时区。Microsoft.Data.Sqlite
        /// 天然按 UTC 往返，且不认识该关键字，故一并丢弃。</item>
        /// </list>
        /// </para>
        /// </summary>
        private static string ToSqliteConnectionString(string connectionString)
        {
            var kept = new List<string>();
            string? busyTimeout = null;
            var hasDefaultTimeout = false;

            foreach (var part in connectionString.Split(';'))
            {
                var segment = part.Trim();
                if (segment.Length == 0)
                {
                    continue;
                }

                var separator = segment.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = segment.Substring(0, separator).Trim();
                var value = segment.Substring(separator + 1).Trim();

                if (key.Equals("BusyTimeout", StringComparison.OrdinalIgnoreCase))
                {
                    busyTimeout = value;
                    continue;
                }

                if (!SqliteKeywords.Contains(key))
                {
                    continue;
                }

                if (key.StartsWith("Default", StringComparison.OrdinalIgnoreCase))
                {
                    hasDefaultTimeout = true;
                }

                kept.Add($"{key}={value}");
            }

            // BusyTimeout(毫秒) → Default Timeout(秒)：保留"锁上等待而不是立即失败"的原意
            if (!hasDefaultTimeout
                && busyTimeout != null
                && long.TryParse(busyTimeout, out var milliseconds)
                && milliseconds > 0)
            {
                kept.Add($"Default Timeout={Math.Max(1, milliseconds / 1000)}");
            }

            return string.Join(";", kept);
        }

    }
}
