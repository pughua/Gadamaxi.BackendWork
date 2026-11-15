using Gadamaxi.BackendWork.Storages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    internal class ServerHeartbeatProcess : IWorkProcess
    {
        private readonly ILogger<ServerHeartbeatProcess> _logger;
        private readonly IDistributedLock _distributedLock;
        private readonly WorkServerOption _option;
        public ServerHeartbeatProcess(ILogger<ServerHeartbeatProcess> logger)
        {
            _logger = logger;
            _distributedLock = DependencyInjectionHelper.Get<IDistributedLock>();
            _option = DependencyInjectionHelper.Get<WorkServerOption>();
        }
        public async Task Execute(WorkProcessContext context)
        {
            var retryCount = 0;
            var ex = await Helper.Retry(async (idx) =>
            {
                retryCount = idx;
                using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
                {
                    await storage.ServerHeartbeat(context.Server.ServerName);
                    await storage.Commit();
                }

                _logger.LogInformation($"{context.Server.ServerName} heartbeat");
            }, Helper.DefaultRetryCount, Helper.DefaultRetryDelay);

            if (ex != null)
            {
                _logger.LogWarning($"Server heartbeat failure.Try again {retryCount} times. {ex.Message}\n{ex.GetStacks()}");
            }
            await ClearTimeOutServer();
            await Helper.Delay(_option.HeartbeatDelay, Helper.DefaultHeartbeatDelay, Helper.One, context.Server.StopToken);
        }

        internal async Task ClearTimeOutServer()
        {
            using (await _distributedLock.AcquireLock(nameof(ServerHeartbeatProcess), Helper.DistributedLockTimeOut))
            {
                using(var storage = DependencyInjectionHelper.Get<IWorkStorage>())
                {
                    // 死判阈值改为可配置（默认 5×心跳间隔），不再硬编码 1 小时，故障发现更及时（#21）。
                    // 过期临界点 = now - 阈值：只删"超过阈值未心跳"的死节点，不误删存活节点。
                    // （原代码用 now + 阈值，配合 RemoveServer 的 < 比较会误删全部节点；且缺 Commit 从未落库，属潜伏 bug。）
                    var expireTime = Helper.CurrentUtcTime().Add(-_option.ServerDeadThreshold);
                    await storage.RemoveServer(expireTime);
                    // 存储是延迟事务，RemoveServer 仅入队 _cache，必须 Commit 才真正删除（#21 修复前清理永不生效）
                    await storage.Commit();
                }
            }
        }
    }
}
