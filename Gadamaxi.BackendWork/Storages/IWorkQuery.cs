using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Sagas;
using Gadamaxi.BackendWork.Works.States;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Storages
{
    public interface IWorkQuery
    {
        Task<List<string>> GetCacheSet(string name, long startTime , long endTime, int batchSize);
        Task<bool> CheckCacheSet(string name, string value);
        Task<RecurringWork?> GetRecurringWorkById(string id);
        Task<WorkData?> GetWorkData(string workId);

        Task<IWorkState?> GetLastState(string wordId);
        Task<int> CountState(string wordId,string stateName);

        /// <summary>
        /// 查询最后状态为 processing 且早于 staleBeforeUtcTime 的工作 Id（用于卡死任务恢复）
        /// </summary>
        Task<List<string>> GetStaleProcessingWorkIds(DateTime staleBeforeUtcTime, int batchSize);


        Task<BaseBatchWork?> GetBatchById(string batchId);
        Task<BatchWork?> GetFullBatchById(string batchId);
        Task<bool> CheckWorkExist(string workId);
        Task<List<BatchItem>> GetBatchItems(List<BatchStatus> statuses, string batchId, int startIdx, int batchSize);
        Task<List<BatchItem>> GetBatchItems(string batchId, int startIdx, int batchSize);
        Task<BatchItem?> GetBatchItem(string batchId, string itemId);
        Task<int> CountBatchItems(string batchId, List<BatchStatus> statuses);

        #region Saga

        Task<SagaInstance?> GetSagaById(string sagaId);

        /// <summary>按业务关联键查询（幂等创建时复用既有 saga）。</summary>
        Task<SagaInstance?> GetSagaByCorrelationId(string correlationId);

        /// <summary>读取 saga 的全部步骤，<b>按序号升序</b>（游标定位依赖该顺序）。</summary>
        Task<List<SagaStep>> GetSagaSteps(string sagaId);

        /// <summary>
        /// 查询"开始时间早于 <paramref name="startedBeforeUtcTime"/> 且仍处于 Running"的 saga Id（整体超时判定）。
        /// 时间比较在 SQL 侧完成：SQLite 读回的 DateTime 是本地时区帧，在 C# 里与 UTC 时间比较会得到错误结论。
        /// </summary>
        Task<List<string>> GetTimeoutSagaIds(DateTime startedBeforeUtcTime, int batchSize);

        #endregion
    }
}
