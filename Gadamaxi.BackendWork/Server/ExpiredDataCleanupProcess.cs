using Gadamaxi.BackendWork.Storages;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    /// <summary>
    /// 过期数据清理进程（#20）：周期性删除已过期且处于终态的工作/批量/saga 及其从表行，避免表无限膨胀。
    /// 与心跳进程类似，每轮执行一次清理后按 CleanupPollingDelay 轮询，停止令牌可随时中断。
    /// </summary>
    internal class ExpiredDataCleanupProcess : IWorkProcess
    {
        private readonly ILogger<ExpiredDataCleanupProcess> _logger;
        private readonly WorkServerOption _option;
        public ExpiredDataCleanupProcess(ILogger<ExpiredDataCleanupProcess> logger)
        {
            _logger = logger;
            _option = DependencyInjectionHelper.Get<WorkServerOption>();
        }

        public async Task Execute(WorkProcessContext context)
        {
            try
            {
                using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
                {
                    var now = Helper.CurrentUtcTime();
                    await storage.RemoveExpiredWorks(now);
                    await storage.RemoveExpiredBatches(now);
                    await storage.RemoveExpiredSagas(now);
                    await storage.RemoveOrphanQueueRows();
                    await storage.Commit();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Expired data cleanup failed. {ex.Message}\n{ex.GetStacks()}");
            }

            await Helper.Delay(_option.CleanupPollingDelay, Helper.DefaultCleanupPollingDelay, Helper.One, context.Server.StopToken);
        }
    }
}
