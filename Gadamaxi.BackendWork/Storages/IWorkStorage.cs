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
    public interface IWorkStorage:IDisposable
    {
        Task Commit();

        /// <param name="name">人工可识别的任务名（监控面板使用）</param>
        Task CreateWork(string workId,Workload work, IDictionary<string, object>? addParameters, DateTime createUtcTime, TimeSpan expireIn, string name);
        Task RemoveWork(string workId);


        Task RemoveRecurringWork(string id);

        /// <summary>无条件更新周期任务（注册/更新路径使用，对应 AddOrUpdate）。</summary>
        Task UpdateRecurringWork(RecurringWork recurringwork);

        /// <summary>
        /// 以版本守卫（CAS）推进周期任务的触发版本（列 LastExecutUtcTime，领域属性 LastExecuteUtcTime）：
        /// 仅当当前 LastExecutUtcTime 仍等于 expectedLastExecuteUtcTime（NULL 视为同一版本）时才推进。
        /// <para>
        /// 必须作为触发事务的<b>第一个写入</b>排队。抢占失败（0 行受影响）时抛出
        /// <see cref="Exceptions.RecurringDuplicateTriggerException"/>，使整个触发事务回滚，
        /// 从而不会创建重复的工作（#11 防重）。存储写入是延迟事务，无法同步返回受影响行数，
        /// 故用"抛异常中止事务"表达"本次触发已被抢先"。
        /// </para>
        /// </summary>
        Task AdvanceRecurringWork(RecurringWork recurringwork, DateTime? expectedLastExecuteUtcTime);

        Task AddRecurringWork(RecurringWork recurringwork);

        Task SetWorkState(string workId, IWorkState initState);
        Task RemoveWorkState(string workId);

        /// <summary>
        /// 发布队列消息。写入被延迟到 <see cref="Commit"/>，与工作行、状态行在同一事务中落库。
        /// 若消息先于工作行可见，消费端会读到"工作不存在"并把消息确认掉，任务将被永久丢弃。
        /// </summary>
        Task Publish(QueueInfo info);

        Task AddCacheSet(string key, double score, string data);
        Task UpdateCacheSet(string key, double score, string data);
        Task RemoveCacheSet(string name, string data);

        Task AddServer(ServerInfo serverInfo);
        Task ServerHeartbeat(string serverName);
        Task RemoveServer(DateTime expireTime);

        /// <summary>
        /// 删除已过期且处于终态（succeeded/deleted）的工作，并级联删除其状态行与队列消息。
        /// 只清理终态任务，避免误删仍在流转（enqueued/processing/scheduled/awaiting）的任务。
        /// </summary>
        Task RemoveExpiredWorks(DateTime now);

        /// <summary>
        /// 删除已过期且处于终态（Success/Failure）的批量，并级联删除其批量项。
        /// </summary>
        Task RemoveExpiredBatches(DateTime now);

        /// <summary>
        /// 删除孤儿队列消息：工作行已不存在的队列行（如 RemoveWork 未清理队列、崩溃残留）。
        /// 工作与队列在同一事务原子提交，未提交的中间态不会被误判为孤儿。
        /// </summary>
        Task RemoveOrphanQueueRows();

        Task AddBatch(BatchWork batch);
        Task UpdateBatch(BaseBatchWork batchWork);
        /// <summary>
        /// 更新批量项状态。
        /// workId 表示子工作 Id：<c>null</c> 表示不改动该列，非空表示写入，
        /// 空串表示<b>清空</b>（批量重试时需要解绑已失败的子工作，让批量项重新展开）。
        /// 批量项需要能回溯到它创建的子工作，批量内的连续任务也依赖该列解析父工作。
        /// </summary>
        Task UpdateBatchItem(string batchId, string batchItemId, BatchStatus status, string message, string? workId = null);

        #region Saga

        /// <summary>
        /// 创建 saga 实例与它的全部步骤（同一事务）。
        /// 调用方还需在同一事务内 <see cref="AddCacheSet"/> 登记 <c>Helper.SagaKey</c>，
        /// 否则 saga 不会被调度进程拾取。
        /// </summary>
        Task AddSaga(SagaInstance saga, IReadOnlyList<SagaStep> steps);

        /// <summary>
        /// 以版本守卫（CAS）推进 saga 的状态与两个游标：仅当当前
        /// (Status, CurrentIndex, CompensationIndex) 三者都等于期望值时才写入。
        /// <para>
        /// 0 行受影响时抛出 <see cref="Exceptions.SagaConcurrentUpdateException"/> 回滚当前事务，
        /// 表示该次推进已被其它推进者处理（延迟事务拿不到同步的受影响行数，只能以抛异常表达失败）。
        /// </para>
        /// 因此<b>每次推进必须使用独立的 <see cref="IWorkStorage"/> 实例</b>，避免把不相关的写入卷进回滚。
        /// </summary>
        Task AdvanceSaga(SagaInstance saga, SagaStatus expectedStatus, int expectedCurrentIndex, int expectedCompensationIndex);

        /// <summary>
        /// 以版本守卫（CAS）展开步骤：仅当步骤当前状态等于 <paramref name="expectedStatus"/> 时才写入，
        /// 保证同一步骤不会被展开两次（0 行 → 抛 <see cref="Exceptions.SagaConcurrentUpdateException"/>）。
        /// </summary>
        /// <param name="workId">
        /// 展开出的工作 Id。<c>null</c> 表示不改动工作 Id 列（如"不可补偿步骤 → Skipped"的标记）。
        /// </param>
        /// <param name="isCompensate">true 时写 <c>CompensateWorkId</c>，false 时写 <c>ForwardWorkId</c></param>
        /// <param name="message"><c>null</c> 表示不改动该列</param>
        Task AdvanceSagaStep(string sagaId, string stepId, SagaStepStatus expectedStatus, SagaStepStatus newStatus,
            string? workId, bool isCompensate, string? message = null);

        /// <summary>
        /// <b>无条件</b>回写步骤状态（幂等目标状态），供状态回写管线使用。
        /// <para>
        /// 这里刻意不用 CAS：一次 <see cref="Commit"/> 内的所有延迟写入是同一个事务，
        /// 若回写在"已被别的写入改过"时抛异常，会把<b>同一事务里的工作状态变更一起回滚</b>——
        /// 而工作确实已经到达终态，状态行必须落库。
        /// 于是分工是：<b>回写（幂等目标状态）无条件 Update；游标/状态推进（必须严格一次）用 CAS</b>。
        /// </para>
        /// </summary>
        /// <param name="message"><c>null</c> 表示不改动该列</param>
        /// <param name="workId"><c>null</c> 表示不改动该列</param>
        Task UpdateSagaStep(string sagaId, string stepId, SagaStepStatus status, string? message, string? workId, bool isCompensate);

        /// <summary>
        /// 列级更新变量池。必须是列级（而非整行 Update）：变量池由步骤工作的状态回写事务写入，
        /// 与 saga 推进事务并发，整行 Update 会让两者互相覆盖（丢失更新）。
        /// </summary>
        Task UpdateSagaVariables(string sagaId, IDictionary<string, string> variables);

        /// <summary>
        /// 删除已过期且处于终态（Completed/Compensated/Aborted）的 saga，并级联删除其步骤。
        /// </summary>
        Task RemoveExpiredSagas(DateTime now);

        #endregion
    }
}
