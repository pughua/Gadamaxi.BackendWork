using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Dashboards
{
    /// <summary>
    /// 面板仓储抽象（核心库持有）。面板层通过 DI 注入使用；
    /// 具体实现放在存储侧（如 Gadamaxi.BackendWork.Sql 的 linq2db 实现 SqlDashboardRepository），
    /// 只依赖核心库，不允许反向引用面板上层；其他数据库自行实现本接口并注册即可替换。
    /// </summary>
    public interface IDashboardRepository
    {
        // ---------- 读查询 ----------

        /// <summary>总览指标。</summary>
        OverviewView GetOverview();

        /// <summary>后台任务分页查询（state/name/queue/workId 可选过滤，null 表示不过滤）。</summary>
        PagedResult<TaskListItem> QueryTasks(string? state, string? name, string? queue, string? workId, int page, int pageSize);

        /// <summary>任务详情（含状态历史与审计）。</summary>
        TaskDetail? GetTaskDetail(string workId);

        /// <summary>并发执行视图（运行中任务、队列、节点维度）。</summary>
        ConcurrencyView GetConcurrency();

        /// <summary>批量任务分页查询（status/name 可选过滤）。</summary>
        PagedResult<BatchListItem> QueryBatches(string? status, string? name, int page, int pageSize);

        /// <summary>批量任务详情（含子任务列表）。</summary>
        BatchDetail? GetBatchDetail(string batchId);

        /// <summary>周期任务分页查询。</summary>
        PagedResult<RecurringItem> QueryRecurrings(int page, int pageSize);

        /// <summary>
        /// 周期任务执行历史。关联依据是工作参数里由调度器写入的 RecurringId，
        /// 而不是任务名——多个周期任务可以同名，按名匹配会把彼此的执行记录混在一起。
        /// </summary>
        PagedResult<TaskListItem> GetRecurringHistory(string recurringId, int page, int pageSize);

        /// <summary>Saga 分页查询（status/stalledOnly 可选过滤）。</summary>
        PagedResult<SagaListItem> QuerySagas(string? status, bool? stalledOnly, int page, int pageSize);

        /// <summary>Saga 详情（含步骤时间线与审计）。</summary>
        SagaDetail? GetSagaDetail(string sagaId);

        /// <summary>集群节点列表（含在线判定与运行中任务数）。</summary>
        IReadOnlyList<NodeItem> GetNodes();

        /// <summary>单个节点信息。</summary>
        NodeItem? GetNode(string serverName);

        // ---------- 写操作支撑（由面板层 DashboardCommands 编排，仓储只负责数据访问） ----------

        /// <summary>读取任务当前状态，不存在返回 null。</summary>
        Task<string?> GetWorkStateAsync(string workId);

        /// <summary>判断周期任务是否存在。</summary>
        Task<bool> RecurringExistsAsync(string recurringId);

        /// <summary>更新周期任务暂停标志，返回受影响行数。</summary>
        Task<int> SetRecurringPausedAsync(string recurringId, bool paused);
    }
}
