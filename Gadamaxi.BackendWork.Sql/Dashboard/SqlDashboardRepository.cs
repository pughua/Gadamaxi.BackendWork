using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Dashboards;
using Gadamaxi.BackendWork.Sagas;
using Gadamaxi.BackendWork.Sql.Tables;
using LinqToDB;
using LinqToDB.Async;
using static Gadamaxi.BackendWork.Dashboards.DashboardModels;

namespace Gadamaxi.BackendWork.Sql
{
    /// <summary>
    /// IDashboardRepository 的 linq2db/关系库实现。直查框架既有表
    /// （Work/State/Queue/Recurring/Batch/BatchItem/Saga/SagaStep/Server），
    /// 复用 DI 中的 DataOptions，不新增连接串。其他数据库形态自行实现 IDashboardRepository 并注册即可。
    /// 原独立项目 Gadamaxi.BackendWork.Sql.Dashboard 已并入本库（2026-10-02）。
    /// </summary>
    public class SqlDashboardRepository : IDashboardRepository
    {
        private readonly DataOptions _dataOptions;
        private readonly BackendWorkDashboardOptions _options;

        public SqlDashboardRepository(DataOptions dataOptions, BackendWorkDashboardOptions options)
        {
            _dataOptions = dataOptions;
            _options = options;
        }

        private DBConnection Db() => new(_dataOptions);

        // ---------- 总览 ----------

        public OverviewView GetOverview()
        {
            using var db = Db();
            var now = DateTime.UtcNow;
            var todayStart = now.Date;
            var offlineBefore = now.AddSeconds(-_options.NodeOfflineThresholdSeconds);

            var stateCounts = db.WorkTable
                .GroupBy(w => w.StateName)
                .Select(g => new { State = g.Key, Cnt = g.Count() })
                .ToList();

            var queues = db.QueueTable
                .GroupBy(q => q.Queue)
                .Select(g => new QueueStat(
                    g.Key,
                    g.Count(),
                    g.Count(x => x.FetchedBy != null)))
                .OrderByDescending(x => x.Depth)
                .ToList();

            var succeededToday = db.StateTable.Count(s => s.Name == "succeeded" && s.CreateUtcTime >= todayStart);
            var failedToday = db.StateTable.Count(s => s.Name == "failed" && s.CreateUtcTime >= todayStart);

            var nodes = db.ServerTable.ToList();
            var online = nodes.Count(n => n.LastHeartbeatUtcTime > offlineBefore);

            var sagaRunning = db.SagaTable.Count(s => s.Status == SagaStatus.Running);
            var sagaCompensating = db.SagaTable.Count(s => s.Status == SagaStatus.Compensating);
            var stalledBefore = now.AddMinutes(-_options.StalledSagaThresholdMinutes);
            var sagaStalled = db.SagaTable.Count(s =>
                s.Status == SagaStatus.Running &&
                (s.UpdateUtcTime ?? s.CreateUtcTime) < stalledBefore);

            var recurringTotal = db.RecurringTable.Count();
            var recurringPaused = db.RecurringTable.Count(r => r.Paused);

            var batchTotal = db.BatchTable.Count();
            var batchRunning = db.BatchTable.Count(b =>
                b.Status == BatchStatus.Awaiting ||
                b.Status == BatchStatus.Init ||
                b.Status == BatchStatus.Processing);
            var batchFailure = db.BatchTable.Count(b => b.Status == BatchStatus.Failure);

            var processing = stateCounts.FirstOrDefault(x => x.State == "processing")?.Cnt ?? 0;
            var enqueued = stateCounts.FirstOrDefault(x => x.State == "enqueued")?.Cnt ?? 0;

            double? failureRate = (succeededToday + failedToday) == 0
                ? null
                : Math.Round((double)failedToday / (succeededToday + failedToday), 4);

            return new OverviewView(
                OnlineNodes: online,
                RegisteredNodes: nodes.Count,
                StateCounts: stateCounts.Select(x => new KeyValuePair<string, int>(x.State, x.Cnt)).ToList(),
                ProcessingTotal: processing,
                EnqueuedTotal: enqueued,
                SucceededToday: succeededToday,
                FailedToday: failedToday,
                FailureRateToday: failureRate,
                Queues: queues,
                RecurringTotal: recurringTotal,
                RecurringPaused: recurringPaused,
                SagaRunning: sagaRunning,
                SagaCompensating: sagaCompensating,
                SagaStalled: sagaStalled,
                TotalWorkers: 0,
                BatchTotal: batchTotal,
                BatchRunning: batchRunning,
                BatchFailure: batchFailure);
        }

        // ---------- 后台任务 ----------

        public PagedResult<TaskListItem> QueryTasks(string? state, string? name, string? queue, string? workId, int page, int pageSize)
        {
            using var db = Db();
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var q = from w in db.WorkTable
                    join qq in db.QueueTable on w.Id equals qq.WorkId into qj
                    from q2 in qj.DefaultIfEmpty()
                    select new { w, q2 };

            if (!string.IsNullOrEmpty(state)) q = q.Where(x => x.w.StateName == state);
            if (!string.IsNullOrEmpty(name)) q = q.Where(x => x.w.Name.Contains(name));
            if (!string.IsNullOrEmpty(queue)) q = q.Where(x => x.q2.Queue == queue);
            // 支持按任务 ID 前缀/片段检索（完整 ID 亦可），便于从日志里粘一段 ID 直接定位
            if (!string.IsNullOrEmpty(workId)) q = q.Where(x => x.w.Id.Contains(workId!));

            // 计数必须对 Work 去重：LEFT JOIN 队列后一个工作有多条队列行时，
            // 直接 Count() 会按行数膨胀，且分页切片会切出重复工作行。
            var total = q.Select(x => x.w.Id).Distinct().Count();
            // 节点名还原在内存里做（NodeName 含字符串截取，无法下推到 SQL）；
            // 用位置构造函数重建记录（不用 with，避免跨程序集 init 访问器的 netstandard2.1 兼容问题）
            var items = q
                .GroupBy(x => x.w.Id)
                .Select(g => g.First())
                .OrderByDescending(x => x.w.CreateUtcTime)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new TaskListItem(
                    x.w.Id, x.w.Name, x.w.StateName,
                    x.q2 != null ? x.q2.Queue : null,
                    x.q2 != null ? x.q2.FetchedBy : null,
                    x.w.CreateUtcTime,
                    0))
                .ToList()
                .Select(x => new TaskListItem(
                    x.Id, x.Name, x.State, x.Queue, NodeName(x.Node),
                    x.CreateUtcTime, x.RetryCount))
                .ToList();

            return new PagedResult<TaskListItem>(items, total);
        }

        public TaskDetail? GetTaskDetail(string workId)
        {
            using var db = Db();
            var work = db.WorkTable.FirstOrDefault(w => w.Id == workId);
            if (work == null) return null;

            var history = db.StateTable
                .Where(s => s.WorkId == workId)
                .OrderBy(s => s.CreateUtcTime)
                .Select(s => new StateHistoryItem(s.Name, DashboardModels.Redact(s.Info, _options) ?? string.Empty, s.CreateUtcTime))
                .ToList();

            var queue = db.QueueTable.FirstOrDefault(q => q.WorkId == workId);

            return new TaskDetail(
                work.Id, work.Name, work.StateName,
                queue?.Queue, NodeName(queue?.FetchedBy),
                work.CreateUtcTime, work.ExpireUtcTime,
                DashboardModels.Redact(work.InvocationJson, _options) ?? string.Empty,
                DashboardModels.Redact(work.ParametersJson, _options) ?? string.Empty,
                history);
        }

        // ---------- 并发 ----------

        public ConcurrencyView GetConcurrency()
        {
            using var db = Db();
            var now = DateTime.UtcNow;

            var processingRaw = (from w in db.WorkTable
                                 join qq in db.QueueTable on w.Id equals qq.WorkId into qj
                                 from q2 in qj.DefaultIfEmpty()
                                 join st in db.StateTable on w.Id equals st.WorkId into sj
                                 from s2 in sj.DefaultIfEmpty()
                                 where w.StateName == "processing" && (s2 == null || s2.Name == "processing")
                                 select new
                                 {
                                     WorkId = w.Id,
                                     Name = w.Name,
                                     RawNode = q2 != null ? q2.FetchedBy : null,
                                     StartTime = (DateTime?)s2.CreateUtcTime
                                 })
                                .ToList();

            // 同一工作可能关联多条 processing 历史，取最早一条作为开始时间
            var processing = processingRaw
                .GroupBy(x => x.WorkId)
                .Select(g => g.OrderBy(x => x.StartTime ?? DateTime.MaxValue).First())
                .Select(x => new ProcessingItem(
                    x.WorkId, x.Name, NodeName(x.RawNode),
                    DashboardModels.Utc(x.StartTime) ?? DateTime.MinValue,
                    x.StartTime == null ? 0 : Math.Round((now - x.StartTime.Value).TotalSeconds, 1)))
                .OrderByDescending(x => x.RunSeconds)
                .ToList();

            var queues = db.QueueTable
                .GroupBy(q => q.Queue)
                .Select(g => new QueueStat(g.Key, g.Count(), g.Count(x => x.FetchedBy != null)))
                .OrderByDescending(x => x.Depth)
                .ToList();

            var nodes = processing
                .GroupBy(x => x.Node ?? "(unknown)")
                .Select(g => new NodeConcurrency(g.Key, g.Count(), 0))
                .OrderByDescending(x => x.Processing)
                .ToList();

            var slow = processing.Count(x => x.RunSeconds >= _options.SlowTaskThresholdSeconds);

            return new ConcurrencyView(processing.Count, slow, queues, nodes, processing);
        }

        // ---------- 批量任务 ----------

        public PagedResult<BatchListItem> QueryBatches(string? status, string? name, int page, int pageSize)
        {
            using var db = Db();
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var q = db.BatchTable.AsQueryable();
            if (!string.IsNullOrEmpty(status) && Enum.TryParse<BatchStatus>(status, true, out var parsed))
                q = q.Where(b => b.Status == parsed);
            if (!string.IsNullOrEmpty(name)) q = q.Where(b => b.Name.Contains(name));

            var total = q.Count();
            var rows = q
                .OrderByDescending(b => b.CreateUtcTime)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .ToList();

            return new PagedResult<BatchListItem>(rows.Select(ToListItem).ToList(), total);
        }

        public BatchDetail? GetBatchDetail(string batchId)
        {
            using var db = Db();
            var batch = db.BatchTable.FirstOrDefault(b => b.BatchId == batchId);
            if (batch == null) return null;

            var items = db.BatchItemTable
                .Where(i => i.BatchId == batchId)
                .OrderBy(i => i.CreateUtcTime).ThenBy(i => i.Name)
                .ToList()
                .Select(i => new BatchItemRow(
                    i.Id, i.Name, i.BatchId, i.Status.ToString(), i.WorkId,
                    DashboardModels.Redact(i.Message, _options) ?? string.Empty,
                    i.CreateUtcTime, i.UpdateUtcTime))
                .ToList();

            return new BatchDetail(ToListItem(batch), items);
        }

        // ---------- 周期任务 ----------

        public PagedResult<RecurringItem> QueryRecurrings(int page, int pageSize)
        {
            using var db = Db();
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var total = db.RecurringTable.Count();
            var items = db.RecurringTable
                .OrderBy(r => r.Name)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(r => new RecurringItem(
                    r.Id, r.Name, r.Cron, r.Queue, r.TimeZone,
                    r.Paused, r.NextExecutUtcTime, r.LastExecutUtcTime,
                    r.LastWorkId, r.RetryCount, r.ErrorInfo))
                .ToList();

            return new PagedResult<RecurringItem>(items, total);
        }

        public PagedResult<TaskListItem> GetRecurringHistory(string recurringId, int page, int pageSize)
        {
            using var db = Db();
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var recurring = db.RecurringTable.FirstOrDefault(r => r.Id == recurringId);
            if (recurring == null) return new PagedResult<TaskListItem>(Array.Empty<TaskListItem>(), 0);

            // 调度器入队时会在工作参数里写入 {"RecurringId":"<id>"}，据此精确关联本次周期任务的历次执行。
            // 不能按 Work.Name 匹配：多个周期任务允许同名（本库就有 4 条同名定义），按名匹配会把彼此记录串在一起，
            // 表现为"cron 是每天 08:00、历史却是每 5 分钟"这种规则与历史不一致的假象。
            var marker = "\"RecurringId\":\"" + recurringId + "\"";
            var q = db.WorkTable.Where(w => w.ParametersJson.Contains(marker));
            var total = q.Count();
            var items = q
                .OrderByDescending(w => w.CreateUtcTime)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(w => new TaskListItem(w.Id, w.Name, w.StateName, recurring.Queue, null, w.CreateUtcTime, 0))
                .ToList();

            return new PagedResult<TaskListItem>(items, total);
        }

        // ---------- Saga ----------

        public PagedResult<SagaListItem> QuerySagas(string? status, bool? stalledOnly, int page, int pageSize)
        {
            using var db = Db();
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var now = DateTime.UtcNow;
            var stalledBefore = now.AddMinutes(-_options.StalledSagaThresholdMinutes);

            var q = db.SagaTable.AsQueryable();
            if (!string.IsNullOrEmpty(status) && Enum.TryParse<SagaStatus>(status, true, out var parsed))
                q = q.Where(s => s.Status == parsed);
            if (stalledOnly == true)
                q = q.Where(s => s.Status == SagaStatus.Running && (s.UpdateUtcTime ?? s.CreateUtcTime) < stalledBefore);

            var total = q.Count();
            var items = q
                .OrderByDescending(s => s.CreateUtcTime)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(s => new SagaListItem(
                    s.SagaId, s.Name, s.CorrelationId, s.Status.ToString(),
                    s.StepCount, s.CurrentIndex, s.CompensationIndex,
                    s.CreateUtcTime, s.UpdateUtcTime, false))
                .ToList();

            // 卡住标记在内存中按阈值补算（阈值可配置，不适合写死进 SQL）；
            // 用位置构造函数重建记录（不用 with，避免跨程序集 init 访问器的 netstandard2.1 兼容问题）
            items = items.Select(x => new SagaListItem(
                x.SagaId, x.Name, x.CorrelationId, x.Status,
                x.StepCount, x.CurrentIndex, x.CompensationIndex,
                x.CreateUtcTime, x.UpdateUtcTime,
                x.Status == nameof(SagaStatus.Running) &&
                (x.UpdateUtcTime ?? x.CreateUtcTime) < stalledBefore)).ToList();

            return new PagedResult<SagaListItem>(items, total);
        }

        public SagaDetail? GetSagaDetail(string sagaId)
        {
            using var db = Db();
            var saga = db.SagaTable.FirstOrDefault(s => s.SagaId == sagaId);
            if (saga == null) return null;

            var steps = db.SagaStepTable
                .Where(x => x.SagaId == sagaId)
                .OrderBy(x => x.Index)
                .Select(x => new SagaStepItem(
                    x.StepId, x.Index, x.Name, x.Status.ToString(),
                    DashboardModels.Redact(x.Message, _options) ?? string.Empty,
                    x.ForwardWorkId, x.CompensateWorkId, x.OutputKey,
                    x.CreateUtcTime, x.UpdateUtcTime))
                .ToList();

            var now = DateTime.UtcNow;
            var stalledBefore = now.AddMinutes(-_options.StalledSagaThresholdMinutes);
            var summary = new SagaListItem(
                saga.SagaId, saga.Name, saga.CorrelationId, saga.Status.ToString(),
                saga.StepCount, saga.CurrentIndex, saga.CompensationIndex,
                saga.CreateUtcTime, saga.UpdateUtcTime,
                saga.Status == SagaStatus.Running && (saga.UpdateUtcTime ?? saga.CreateUtcTime) < stalledBefore);

            return new SagaDetail(
                summary,
                DashboardModels.Redact(saga.VariablesJson, _options) ?? string.Empty,
                DashboardModels.Redact(saga.Message, _options) ?? string.Empty,
                saga.ExpireUtcTime,
                steps);
        }

        // ---------- 节点 ----------

        public IReadOnlyList<NodeItem> GetNodes()
        {
            using var db = Db();
            var now = DateTime.UtcNow;
            var offlineBefore = now.AddSeconds(-_options.NodeOfflineThresholdSeconds);

            var nodes = db.ServerTable.OrderByDescending(n => n.LastHeartbeatUtcTime).ToList();
            // FetchedBy 带一次性抢占后缀，必须还原成节点名才能和 ServerName 对上；
            // 口径与并发视图一致：只算对应工作仍处于 processing 的队列行（抢占未释放 ≠ 正在执行）
            var processing = (from q in db.QueueTable
                              join w in db.WorkTable on q.WorkId equals w.Id
                              where q.FetchedBy != null && w.StateName == "processing"
                              select q.FetchedBy!)
                             .ToList()
                             .GroupBy(f => NodeName(f) ?? string.Empty)
                             .ToDictionary(g => g.Key, g => g.Count());

            return nodes.Select(n =>
            {
                List<string> queues = new();
                try
                {
                    var data = System.Text.Json.JsonSerializer.Deserialize<ServerDataPayload>(n.JsonData ?? "{}");
                    if (data?.Queues != null) queues = data.Queues;
                }
                catch { /* JsonData 解析失败不影响节点列表展示 */ }

                return new NodeItem(
                    n.ServerName,
                    n.LastHeartbeatUtcTime > offlineBefore,
                    n.CreateUtcTime,
                    n.LastHeartbeatUtcTime,
                    queues,
                    processing.TryGetValue(n.ServerName, out var c) ? c : 0);
            }).ToList();
        }

        public NodeItem? GetNode(string serverName)
        {
            return GetNodes().FirstOrDefault(n => n.ServerName == serverName);
        }

        // ---------- 写操作支撑 ----------

        public async Task<string?> GetWorkStateAsync(string workId)
        {
            using var db = Db();
            return await db.WorkTable
                .Where(w => w.Id == workId)
                .Select(w => w.StateName)
                .FirstOrDefaultAsync();
        }

        public async Task<bool> RecurringExistsAsync(string recurringId)
        {
            using var db = Db();
            return await db.RecurringTable.AnyAsync(r => r.Id == recurringId);
        }

        public async Task<int> SetRecurringPausedAsync(string recurringId, bool paused)
        {
            using var db = Db();
            return await db.RecurringTable
                .Where(r => r.Id == recurringId)
                .Set(r => r.Paused, paused)
                .UpdateAsync();
        }

        // ---------- 内部 ----------

        private BatchListItem ToListItem(BatchTable b) => new(
            b.BatchId, b.Name, b.ParentBatchId, b.Status.ToString(),
            b.Count, b.DealCount, b.RetryCount,
            DashboardModels.Redact(b.Message, _options) ?? string.Empty,
            b.CreateUtcTime, b.UpdateUtcTime, b.ExpireUtcTime);

        private sealed class ServerDataPayload
        {
            public List<string> Queues { get; set; } = new();
        }
    }
}
