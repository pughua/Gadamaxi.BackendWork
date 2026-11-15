using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    /// <summary>
    /// 卡死任务恢复进程：
    /// 周期性检查最后状态为 processing 且超过 WorkProcessingTimeout 的工作，
    /// 在能取得工作锁（执行者存活时会持锁并持续续期，取不到锁说明仍在执行）的前提下重新入队，
    /// 解决执行节点崩溃导致任务永久卡在 processing 的问题。
    /// </summary>
    internal class RecoveryWorkProcess : IWorkProcess
    {
        private readonly IWorkQuery _workQuery;
        private readonly IDistributedLock _distributedLock;
        private readonly ILogger<RecoveryWorkProcess> _logger;
        private readonly WorkServerOption _option;

        public RecoveryWorkProcess(ILogger<RecoveryWorkProcess> logger)
        {
            _workQuery = DependencyInjectionHelper.Get<IWorkQuery>();
            _distributedLock = DependencyInjectionHelper.Get<IDistributedLock>();
            _logger = logger;
            _option = DependencyInjectionHelper.Get<WorkServerOption>();
        }

        public async Task Execute(WorkProcessContext context)
        {
            var count = Helper.Zero;
            do
            {
                count = await RecoverStaleWorks();

                if (count <= Helper.Zero)
                {
                    await Helper.Delay(_option.RecoveryPollingDelay, Helper.DefaultRecoveryPollingDelay, count, context.Server.StopToken);
                }
            }
            while (count > Helper.Zero);
        }

        /// <summary>
        /// 恢复一轮卡死任务，返回本轮扫描到的卡死任务数量（无进展时返回 0 以结束本轮）。
        /// </summary>
        internal async Task<int> RecoverStaleWorks()
        {
            var cutoff = Helper.CurrentUtcTime() - _option.WorkProcessingTimeout;
            var ids = await _workQuery.GetStaleProcessingWorkIds(cutoff, _option.GetBatchSize());
            if (ids.Count <= Helper.Zero)
            {
                return ids.Count;
            }

            _logger.LogTrace($"Fetch {ids.Count} stale processing work.");
            var requeuedCount = Helper.Zero;
            foreach (var id in ids)
            {
                try
                {
                    // 陈旧性已由 GetStaleProcessingWorkIds 的 SQL 查询判定；
                    // 此处仅校验最后状态仍为 processing（状态读取回的时间帧因数据库而异，不可再与本地时间比较）：
                    // 若任务在扫描后已完成，RequeueWork 的 Finish 校验会跳过；若转 failed/scheduled，状态名校验会跳过
                    var requeued = await WorkFactory.RequeueWork(_workQuery, _distributedLock, id,
                        nameof(RecoveryWorkProcess),
                        last => last is ProcessingWorkState);
                    if (requeued)
                    {
                        requeuedCount++;
                        _logger.LogInformation($"Stale processing work {id} has been recovered to queue.");
                    }
                }
                catch (LockBackendException)
                {
                    // 工作锁被持有并续期中，说明任务正在被执行，跳过，等下一轮再检查
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Recover stale processing work {id} failure.{ex.Message}\n{ex.GetStacks()}");
                }
            }

            if (requeuedCount <= Helper.Zero)
            {
                // 本轮扫描到的卡死任务全部被执行锁占用（无进展），返回 0 避免热循环
                return Helper.Zero;
            }

            return ids.Count;
        }
    }
}
