using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Resources;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Batchs
{
    public class BatchWorkProcess : IWorkProcess
    {
        private readonly IDistributedLock _distributedLock;
        private readonly IWorkQuery _workQuery;
        private readonly ILogger<BatchWorkProcess> _logger;
        private readonly IWorkQueue _workQueue;
        private readonly IWorkStateHandlerProvider _provider;
        private readonly WorkServerOption _option;
        public BatchWorkProcess(IDistributedLock distributedLock,
            IWorkQuery workQuery,
            IWorkQueue workQueue,
            IWorkStateHandlerProvider provider,
            ILogger<BatchWorkProcess> logger,
            WorkServerOption option)
        {
            _distributedLock = distributedLock;
            _workQuery = workQuery;
            _logger = logger;
            _workQueue = workQueue;
            _provider = provider;
            _option = option;
        }

        public async Task Execute(WorkProcessContext context)
        {
            var count = Helper.Zero;

            do
            {
                count = Helper.Zero;
                var ids = await WorkFactory.GetCacheDatas(_workQuery, _distributedLock, Helper.BatchKey,_option.SchedulePollingDelay);

                foreach (var id in ids)
                {
                    try
                    {
                        await ExecuteBatch(id);
                        count++;
                    }
                    catch (LockBackendException ex)
                    {
                        _logger.LogTrace(ex.Message);
                    }
                }
            } while (count > Helper.Zero);

            if (count <= Helper.Zero)
            {
                await Helper.Delay(_option.SchedulePollingDelay, Helper.DefaultSchedulerPollingDelay, count, context.Server.StopToken);
            }
        }

        private async Task ExecuteBatch(string id)
        {
            using (await _distributedLock.AcquireLock(id, Helper.DistributedLockTimeOut))
            {
                var batchWork = await _workQuery.GetBatchById(id);
                await UpdateCacheSetTime(id, batchWork);
                if (batchWork == null)
                {
                    _logger.LogTrace($"Batch {id} removed.");
                    return;
                }

                if (batchWork.Finish())
                {
                    _logger.LogTrace($"Batch {id} success. No need to execute.");
                    return;
                }

                Exception? exinfo = null;
                List<BatchItem> items = new List<BatchItem>();

                do
                {
                    items = await _workQuery.GetBatchItems( new List<BatchStatus> { BatchStatus.Init }, id, Helper.Zero, _option.GetBatchSize());
                    foreach(var item in items)
                    {
                        var ex = await ExecuteBatchItem(batchWork, item);
                        if (ex != null)
                        {
                            exinfo = ex;
                        }

                        await Task.Delay(10);
                    }
                }
                while (items.Count > Helper.Zero);

                if (exinfo != null)
                {
                    using (var stor = DependencyInjectionHelper.Get<IWorkStorage>())
                    {
                        await BatchFactory.ChangeStatus(stor,
                            batchWork,
                            BatchStatus.Failure,
                            $"{nameof(BatchStatus.Failure)},retry {batchWork.RetryCount} times.");

                        // 存储写入是延迟事务：不提交则"批量失败"这一状态写不进库。
                        // 漏掉后批量既不会失败也不会终止——每轮都被重新拉取，
                        // 而失败的批量项已是 Processing 会被跳过，失败对使用者完全不可见。
                        await stor.Commit();
                    }

                    _logger.LogTrace($"Executed batch {batchWork.BatchId} {nameof(BatchStatus.Failure)}.");
                }
                else
                {
                    await RefreshBatchStatus(batchWork);
                }
            }

        }

        /// <summary>
        /// 按批量项的<b>实际执行结果</b>刷新批量状态：全部成功才算成功，任一项失败即为失败。
        /// 原先以"子工作已创建数量"判定成功，会在子任务尚未执行完（甚至已失败）时就把批量标记成功，
        /// 既让监控失真，也让用户无从针对失败的子任务发起重试。
        /// 判定依赖已提交的批量项状态，故只在展开流程正常时调用（展开异常已单独标记失败）。
        /// </summary>
        private async Task RefreshBatchStatus(BaseBatchWork batchWork)
        {
            var success = await _workQuery.CountBatchItems(batchWork.BatchId,
                new List<BatchStatus> { BatchStatus.Success });
            var failure = await _workQuery.CountBatchItems(batchWork.BatchId,
                new List<BatchStatus> { BatchStatus.Failure });

            BatchStatus? target = null;
            if (failure > Helper.Zero)
            {
                target = BatchStatus.Failure;
            }
            else if (success >= batchWork.Count)
            {
                target = BatchStatus.Success;
            }

            // 已处于目标终态时不再重复写入：Failure -> Failure 会累加 RetryCount
            if (target == null || target.Value == batchWork.Status)
            {
                return;
            }

            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                await BatchFactory.ChangeStatus(storage,
                    batchWork,
                    target.Value,
                    $"{target.Value},success {success}/{batchWork.Count},failure {failure}.");
                await storage.Commit();
            }

            _logger.LogTrace($"Executed batch {batchWork.BatchId} {target}.");
        }

        private async Task UpdateCacheSetTime(string batchId,BaseBatchWork? batchWork)
        {
            using(var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                if(batchWork == null)
                {
                    await storage.RemoveCacheSet(Helper.BatchKey, batchId);
                }
                else
                {
                    // 处理完要把缓存时间推后一个轮询周期。写成"当前时间"会让同一个批量
                    // 在 do-while 里被反复拉取：批量要等子任务执行完才可能完成，
                    // 已经不能靠"完成后移除缓存"来结束循环了。
                    await storage.UpdateCacheSet(Helper.BatchKey,
                        Helper.CurrentUtcTime().Add(_option.SchedulePollingDelay).UnixMillisecondStamp(),
                        batchId);
                }

                // 存储写入是延迟事务，不提交则本次缓存时间更新/清理不会落库
                await storage.Commit();
            }
        }

        private async Task<Exception?> ExecuteBatchItem(BaseBatchWork batchWork,BatchItem item)
        {
            if(item.Status>= BatchStatus.Processing)
            {
                return null; ;
            }

            Exception? result = null;
            var paramters = new Dictionary<string, Object>();
            IWorkState? initSate = null;
            if (item.Data is BatchItemWorkData workData)
            {
                initSate = WorkFactory.ConvertState(new EmptyWorkState(workData.State.StateName,
                    workData.State.Info,
                    workData.State.CreateUtcTime,
                    workData.State.Data));



                if (workData.Parameters != null)
                {
                    foreach (var p in workData.Parameters)
                    {
                        paramters.Add(p.Key, p.Value);
                    }
                }

                //deal parentId
                if (initSate is AwaitingWorkState awaitState)
                {
                    var pitem = await _workQuery.GetBatchItem(batchWork.BatchId, awaitState.ParentId);

                    if (pitem != null && string.IsNullOrEmpty(pitem.WorkId))
                    {
                        _logger.LogTrace($"batch item {batchWork.BatchId}:{item.Id},parent {pitem.Id} work not created .");
                        return null;
                    }

                    if (pitem != null && !string.IsNullOrEmpty(pitem.WorkId))
                    {
                        awaitState.ParentId = pitem.WorkId;
                    }
                }
            }
            
            paramters.Add(Helper.BatchIdKey, batchWork.BatchId);
            paramters.Add(Helper.BatchItemIdKey, item.Id);

            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                try
                {
                    
                    item.Status = BatchStatus.Processing;

                    if (initSate!=null)
                    {
                        item.WorkId = await WorkFactory.CreateWork(storage,
                            _workQueue,
                            _provider,
                            item.Work,
                            Helper.CurrentUtcTime(),
                            string.Format(Resource.TriggerBy, this.GetType().Name),
                            initSate,
                            paramters,
                            item.Name);
                    }
                    else
                    {
                        item.WorkId = await WorkFactory.CreateEnqueueWork(storage,
                            _workQueue,
                            _provider,
                            item.Work,
                            Helper.CurrentUtcTime(),
                            string.Format(Resource.TriggerBy, this.GetType().Name),
                            paramters,
                            item.Name);
                    }

                    batchWork.DealCount += Helper.One;
                    
                    _logger.LogTrace($"Executed batch item {batchWork.BatchId}:{item.Id}.");
                }
                catch(Exception ex)
                {
                    item.Message = ex.Message;
                    _logger.LogWarning($"Execute batch item {batchWork.BatchId}:{item.Id}.{ex.Message}\n{ex.GetStacks()}");
                    result = ex;
                }
                // 一并把子工作 Id 落库：批量项要能回溯子工作，批量内的连续任务也要靠它解析父工作
                await storage.UpdateBatchItem(batchWork.BatchId, item.Id, item.Status, item.Message, item.WorkId);

                await storage.UpdateBatch(batchWork);


                await storage.Commit();

                // 子工作落库之后再判定父工作是否已结束：
                // 只有提交之后"子任务注册"与"父任务扫描等待集合"才有确定的先后顺序，
                // 否则可能两者互相错过（父任务扫描时子任务还没注册，子任务自查时父任务还没落库完成状态），
                // 连续子任务会永久停留在 awaiting。
                if (initSate is AwaitingWorkState awaitState2 && !string.IsNullOrEmpty(item.WorkId))
                {
                    await WorkFactory.EnqueueAwaitingWorkIfParentFinished(_workQuery,
                        item.WorkId, awaitState2.ParentId);
                }
            }

            return result;
        }
    }
}
