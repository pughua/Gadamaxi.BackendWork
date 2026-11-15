using System;
using System.Threading.Tasks;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Sql;
using LinqToDB;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 验证心跳死判（#21）：
    /// 1) 只删"超过阈值未心跳"的死节点，不误删存活节点（原实现缺 Commit 且用 now+阈值会误删全部）；
    /// 2) 阈值边界内（含边界）的节点保留，超出阈值的节点被清理。
    /// </summary>
    public class ServerHeartbeatTests : BackendWorkTestBase
    {
        private Task SeedServer(string name, DateTime heartbeat) => Db.SeedServerAsync(name, heartbeat);

        private Task<bool> ServerExists(string name) => Db.ServerExistsAsync(name);

        [Fact]
        public async Task ClearTimeOutServer_只删死节点_不误删存活节点()
        {
            // 阈值 30 分钟：超过 30 分钟未心跳判死
            Option.ServerDeadThreshold = TimeSpan.FromMinutes(30);
            var now = Helper.CurrentUtcTime();

            await SeedServer("alive-server", now.AddMinutes(-1));   // 1 分钟前心跳：存活
            await SeedServer("dead-server", now.AddMinutes(-60));    // 60 分钟前心跳：已死

            var process = new ServerHeartbeatProcess(CreateLogger<ServerHeartbeatProcess>());
            await process.ClearTimeOutServer();

            Assert.True(await ServerExists("alive-server"), "存活节点不应被清理");
            Assert.False(await ServerExists("dead-server"), "死节点应被清理");
        }

        [Fact]
        public async Task ClearTimeOutServer_阈值内保留_超出阈值清理()
        {
            Option.ServerDeadThreshold = TimeSpan.FromMinutes(30);
            var now = Helper.CurrentUtcTime();

            await SeedServer("within-server", now.AddMinutes(-29));  // 29 分钟前：阈值内，保留
            await SeedServer("beyond-server", now.AddMinutes(-31));  // 31 分钟前：超出阈值，清理

            var process = new ServerHeartbeatProcess(CreateLogger<ServerHeartbeatProcess>());
            await process.ClearTimeOutServer();

            Assert.True(await ServerExists("within-server"), "阈值内的节点应保留");
            Assert.False(await ServerExists("beyond-server"), "超出阈值的节点应被清理");
        }
    }
}
