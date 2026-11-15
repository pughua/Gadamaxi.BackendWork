using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// saga 整体超时进程：把"开始时间已超过 SagaTimeout 且仍处于 Running"的 saga 转入补偿。
    /// <para>
    /// 结构照 <c>RecoveryWorkProcess</c>：自己轮询、自己扫描、每轮处理一批。
    /// 与推进进程分开而不是合并，是因为超时判定的粒度本来就是分钟级，
    /// 没必要跟着 1 秒的推进轮询一起扫库（合并后只是白白增加查询量）。
    /// </para>
    /// </summary>
    internal class SagaTimeoutProcess : IWorkProcess
    {
        private readonly IWorkQuery _workQuery;
        private readonly IDistributedLock _distributedLock;
        private readonly ILogger<SagaTimeoutProcess> _logger;
        private readonly WorkServerOption _option;

        public SagaTimeoutProcess(IWorkQuery workQuery,
            IDistributedLock distributedLock,
            ILogger<SagaTimeoutProcess> logger,
            WorkServerOption option)
        {
            _workQuery = workQuery;
            _distributedLock = distributedLock;
            _logger = logger;
            _option = option;
        }

        public async Task Execute(WorkProcessContext context)
        {
            try
            {
                await TimeoutSagas();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Saga timeout scan failure.{ex.Message}\n{ex.GetStacks()}");
            }

            await Helper.Delay(_option.SagaTimeoutPollingDelay, Helper.DefaultSagaTimeoutPollingDelay,
                Helper.One, context.Server.StopToken);
        }

        /// <summary>
        /// 扫描一轮超时 saga，返回本轮真正转入补偿的数量。
        /// </summary>
        internal async Task<int> TimeoutSagas()
        {
            var startedBefore = Helper.CurrentUtcTime() - _option.SagaTimeout;
            var ids = await _workQuery.GetTimeoutSagaIds(startedBefore, _option.GetBatchSize());
            if (ids.Count <= Helper.Zero)
            {
                return ids.Count;
            }

            _logger.LogTrace($"Fetch {ids.Count} timeout saga.");

            var timeoutCount = Helper.Zero;
            foreach (var id in ids)
            {
                try
                {
                    if (await TimeoutSaga(id))
                    {
                        timeoutCount++;
                    }
                }
                catch (LockBackendException)
                {
                    // 锁被推进进程持有：说明正在被推进，等下一轮再判定
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Timeout saga {id} failure.{ex.Message}\n{ex.GetStacks()}");
                }
            }

            return timeoutCount;
        }

        private async Task<bool> TimeoutSaga(string sagaId)
        {
            using (await _distributedLock.AcquireLock(sagaId, Helper.DistributedLockTimeOut))
            {
                var saga = await _workQuery.GetSagaById(sagaId);

                // 截止时间已在 SQL 侧判定完毕（时间比较不能在 C# 里做：SQLite 读回的是本地时区帧）。
                // 这里只校验阶段仍是 Running：扫描之后可能已推进完成、已被取消、或已转入补偿
                if (saga == null || saga.Status != SagaStatus.Running)
                {
                    return false;
                }

                using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
                {
                    // 走与"正向失败"相同的回滚路径
                    var started = await SagaFactory.Wake(storage, _logger, saga,
                        SagaStatus.Compensating,
                        saga.CurrentIndex,
                        saga.CurrentIndex - Helper.One,
                        $"timeout: running longer than {_option.SagaTimeout}.");

                    if (!started)
                    {
                        // CAS 被抢先：别的推进者已经处理过，不算本次超时
                        return false;
                    }

                    _logger.LogInformation($"Saga {sagaId} timed out, compensating from step {saga.CompensationIndex}.");
                    return true;
                }
            }
        }
    }
}
