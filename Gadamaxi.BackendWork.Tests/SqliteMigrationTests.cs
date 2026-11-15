using FluentMigrator.Runner;
using FluentMigrator.Runner.Versioning;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 验证测试库结构来自生产迁移脚本（Gadamaxi.BackendWork.Sql/DBMigrators/InitTable），
    /// 而不是测试自己维护的手工建表 SQL：测试基类通过 IMigrationRunner.MigrateUp() 建库。
    /// </summary>
    public class SqliteMigrationTests : BackendWorkTestBase
    {
        [SqlBackendOnlyFact]
        public async Task 迁移后_所有业务表均可访问()
        {
            using (var db = OpenDb())
            {
                Assert.Equal(0, await db.CacheSetTable.CountAsync());
                Assert.Equal(0, await db.LockTable.CountAsync());
                Assert.Equal(0, await db.QueueTable.CountAsync());
                Assert.Equal(0, await db.RecurringTable.CountAsync());
                Assert.Equal(0, await db.ServerTable.CountAsync());
                Assert.Equal(0, await db.StateTable.CountAsync());
                Assert.Equal(0, await db.WorkTable.CountAsync());
                Assert.Equal(0, await db.BatchTable.CountAsync());
                Assert.Equal(0, await db.BatchItemTable.CountAsync());
                Assert.Equal(0, await db.SagaTable.CountAsync());
                Assert.Equal(0, await db.SagaStepTable.CountAsync());
            }
        }

        [SqlBackendOnlyFact]
        public void 迁移后_版本表记录全部迁移()
        {
            var loader = Provider.GetRequiredService<IVersionLoader>();
            loader.LoadVersionInfo();

            // 最新版本号必须随 DBMigrators 里新增的迁移同步推进
            // （1000 = InitTable；AddWorkName/AddQueueFetchedBy/AddSagaTables/AddDashboardSupport
            //   的 DDL 已合并进 InitTable，不再作为独立迁移存在），否则这里会假红
            Assert.Equal(1000L, loader.VersionInfo.Latest());
            Assert.Contains(1000L, loader.VersionInfo.AppliedMigrations());
        }

        [SqlBackendOnlyFact]
        public void 迁移后_名称列已存在()
        {
            // AddWorkName(1001) 追加的 Name 列
            using (var db = OpenDb())
            {
                Assert.Equal(0, db.WorkTable.Where(t => t.Name == "not-exist-name").Count());
                Assert.Equal(0, db.BatchTable.Where(t => t.Name == "not-exist-name").Count());
                Assert.Equal(0, db.BatchItemTable.Where(t => t.Name == "not-exist-name").Count());
            }
        }

        [SqlBackendOnlyFact]
        public void 迁移后_队列抢占标记列已存在()
        {
            // AddQueueFetchedBy(1002) 追加的 FetchedBy 列
            using (var db = OpenDb())
            {
                Assert.Equal(0, db.QueueTable.Where(t => t.FetchedBy == "not-exist-token").Count());
            }
        }

        [SqlBackendOnlyFact]
        public void 迁移可重复执行_不会重复建表()
        {
            // 已迁移到最新版本的库再次 MigrateUp 应为空操作（版本表已记录 InitTable）
            Provider.GetRequiredService<IMigrationRunner>().MigrateUp();
        }
    }
}
