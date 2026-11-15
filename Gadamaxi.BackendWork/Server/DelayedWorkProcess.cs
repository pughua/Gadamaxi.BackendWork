using Gadamaxi.BackendWork.Resources;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    internal class DelayedWorkProcess : IWorkProcess
    {
        private readonly IDistributedLock _distributedLock;
        private readonly IWorkQuery _workQuery;
        private readonly ILogger<DelayedWorkProcess> _logger;
        private readonly WorkServerOption _option;
        public DelayedWorkProcess(ILogger<DelayedWorkProcess> logger) 
        {
            _distributedLock = DependencyInjectionHelper.Get<IDistributedLock>();
            _workQuery = DependencyInjectionHelper.Get<IWorkQuery>();
            _logger = logger;
            _option = DependencyInjectionHelper.Get<WorkServerOption>();
        }

        public async Task Execute(WorkProcessContext context)
        {
            var count = Helper.Zero;

            do
            {
                var ids = await WorkFactory.GetCacheDatas(_workQuery, _distributedLock, ScheduledWorkState.Name,_option.SchedulePollingDelay);
                count = ids.Count;
                if (count > Helper.Zero)
                {
                    _logger.LogTrace($"Fetch {ids.Count} scheduleWork.");

                    foreach (var id in ids)
                    {
                        var retryCount = 0;
                        var ex = await Helper.Retry(async (idx) =>
                        {
                            retryCount = idx;
                            await EnqueueWork(id);
                        }, Helper.DefaultRetryCount, Helper.DefaultRetryDelay);
                        if (ex != null)
                        {
                            _logger.LogWarning($"ScheduleWork {id} enqueue failure.Try again {retryCount} times. {ex.Message}\n{ex.GetStacks()}");
                        }
                        else
                        {
                            _logger.LogTrace($"ScheduleWork {id} enqueue.");
                            await Task.Delay(Helper.DefaultWorkIntervalDelay);
                        }
                    }
                }
            }
            while (count > Helper.Zero);


            if (count <= Helper.Zero)
            {
                await Helper.Delay(_option.SchedulePollingDelay, Helper.DefaultSchedulerPollingDelay, count, context.Server.StopToken);
            }
        }

        private async Task EnqueueWork(string workId)
        {
            // 复用 WorkFactory.RequeueWork 通用重投实现（与 RecoveryWorkProcess 共用）
            var requeued = await WorkFactory.RequeueWork(_workQuery, _distributedLock, workId, this.GetType().Name);
            if (requeued)
            {
                _logger.LogInformation($"ScheduleWork {workId} enqueue success.");
            }
            else
            {
                _logger.LogTrace($"ScheduleWork {workId} skip enqueue (work missing or finished).");
            }
        }
    }
}
