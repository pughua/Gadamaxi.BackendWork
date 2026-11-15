using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Batchs
{
    public class BatchManager : IBatchManager
    {
        private readonly ILogger<BatchManager> _logger;
        private readonly IWorkQuery _workQuery;
        public BatchManager(
            IWorkQuery workQuery,
            ILogger<BatchManager> logger) 
        {
            _workQuery = workQuery;
            _logger = logger;
        }

        public async Task<string> Create(Action<BatchContext> methodCall, string name)
        {
            Helper.CheckArgumentNull(methodCall, nameof(methodCall));
            return await CreateInner(string.Empty, methodCall, name, BatchStatus.Awaiting);
        }

        public async Task<string> CreateContinue(string parentBatchId, Action<BatchContext> methodCall, string name)
        {
            Helper.CheckArgumentNull(parentBatchId, nameof(parentBatchId));
            Helper.CheckArgumentNull(methodCall, nameof(methodCall));
            return await CreateInner(parentBatchId, methodCall, name, BatchStatus.Awaiting);
        }

        public async Task Retry(string batchId)
        {
            Helper.CheckArgumentNull(batchId, nameof(batchId));

            var batchWork = await _workQuery.GetBatchById(batchId);
            if (batchWork == null)
            {
                throw new BackendException($"Batch {batchId} not exists.");
            }

            CheckRetryable(batchWork);

            var items = await GetAllItems(batchId);
            var retryItems = new List<BatchItem>();
            var keepCount = Helper.Zero;

            foreach (var item in items)
            {
                // 已成功或仍在执行中的子任务都不重复执行，只有失败/未执行的才重新展开
                if (item.Status == BatchStatus.Success || await ItemRunning(item))
                {
                    keepCount++;
                }
                else
                {
                    retryItems.Add(item);
                }
            }

            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                foreach (var item in retryItems)
                {
                    // 清空子工作 Id：批量项重新展开后会创建新的子工作
                    await storage.UpdateBatchItem(batchId, item.Id,
                        BatchStatus.Init, string.Empty, string.Empty);
                }

                batchWork.DealCount = keepCount;
                batchWork.RetryCount += Helper.One;

                // 失败批量仍留在调度缓存里，重新登记前先移除，避免撞上 (Type,Value) 唯一约束
                await storage.RemoveCacheSet(Helper.BatchKey, batchId);

                await BatchFactory.ChangeStatus(storage,
                    batchWork,
                    BatchStatus.Init,
                    $"retry {retryItems.Count} item(s),skip {keepCount}.");

                await storage.Commit();
            }

            _logger.LogInformation($"Retry batch {batchId},retry {retryItems.Count} item(s),skip {keepCount}.");
        }

        private static void CheckRetryable(BaseBatchWork batchWork)
        {
            if (batchWork.Status == BatchStatus.Success)
            {
                throw new BackendException($"Batch {batchWork.BatchId} already {BatchStatus.Success}.");
            }

            // None：尚未进入调度；Awaiting：还在等父批量，重试都没有意义
            if (batchWork.Status == BatchStatus.None
                || batchWork.Status == BatchStatus.Awaiting)
            {
                throw new BackendException($"Batch {batchWork.BatchId} status {batchWork.Status} can not retry.");
            }
        }

        /// <summary>批量项的子工作是否仍在执行链路中（执行中的不能重复发起）</summary>
        private async Task<bool> ItemRunning(BatchItem item)
        {
            if (string.IsNullOrEmpty(item.WorkId))
            {
                return false;
            }

            if (!await _workQuery.CheckWorkExist(item.WorkId))
            {
                return false;
            }

            var lastState = await _workQuery.GetLastState(item.WorkId);
            if (lastState == null)
            {
                return false;
            }

            if (lastState.Finish)
            {
                return false;
            }

            if (lastState.StateName != FailedWorkState.Name)
            {
                // enqueued / processing / scheduled / awaiting：仍会被调度执行
                return true;
            }

            // failed 且重试次数已用尽时工作不会再被调度，可以重新展开
            return !WorkFactory.RetryExhausted(await _workQuery.CountState(item.WorkId, FailedWorkState.Name));
        }

        private async Task<List<BatchItem>> GetAllItems(string batchId)
        {
            var result = new List<BatchItem>();
            var startIdx = Helper.Zero;

            while (true)
            {
                var page = await _workQuery.GetBatchItems(batchId, startIdx, Helper.BatchSize);
                result.AddRange(page);
                if (page.Count < Helper.BatchSize)
                {
                    break;
                }

                startIdx += page.Count;
            }

            return result;
        }

        private async Task<string> CreateInner(string parentBatchId,
            Action<BatchContext> methodCall, string name, BatchStatus initStatus)
        {
            // 批量名上限比普通任务名小：需要为子任务的 "#序号" 后缀预留长度
            Helper.CheckWorkName(name, Helper.MaxBatchNameLength);

            var ctx = new BatchContext();
            // 批量名先行设置：BatchContext.AddWork 依此派生每个子任务名
            ctx.Batch.Name = name;
            if (!string.IsNullOrEmpty(parentBatchId))
            {
                ctx.Batch.ParentBatchId = parentBatchId;
            }
            methodCall(ctx);

            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                await storage.AddBatch(ctx.Batch);

                await BatchFactory.ChangeStatus(storage,
                    ctx.Batch,
                    initStatus,
                    string.Empty);

                await storage.Commit();
            }

            _logger.LogInformation($"Created batch {ctx.Batch.BatchId}");
            return ctx.Batch.BatchId;
        }
    }
}
