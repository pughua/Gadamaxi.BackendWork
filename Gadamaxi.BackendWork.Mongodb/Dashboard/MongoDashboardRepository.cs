using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Dashboards;
using Gadamaxi.BackendWork.Mongodb.Documents;
using Gadamaxi.BackendWork.Sagas;
using MongoDB.Bson;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static Gadamaxi.BackendWork.Dashboards.DashboardModels;

namespace Gadamaxi.BackendWork.Mongodb
{
    /// <summary>
    /// IDashboardRepository 的 Mongodb 实现。直查框架既有集合
    /// （Work/State/Queue/Recurring/Batch/BatchItem/Saga/SagaStep/Server），
    /// 语义与 <c>SqlDashboardRepository</c> 一一对应（节点名还原、周期历史按参数里的 RecurringId 关联、
    /// Saga 卡住阈值内存补算、脱敏截断、节点在线判定），宿主换后端无需改面板。
    /// </summary>
    public class MongoDashboardRepository : IDashboardRepository
    {
        private readonly MongoBackendWorkContext _context;
        private readonly BackendWorkDashboardOptions _options;

        public MongoDashboardRepository(MongoBackendWorkContext context, BackendWorkDashboardOptions options)
        {
            Helper.CheckArgumentNull(context, nameof(context));

            _context = context;
            _options = options ?? new BackendWorkDashboardOptions();
        }

        // ---------- 总览 ----------

        public OverviewView GetOverview()
        {
            var now = DateTime.UtcNow;
            var todayStart = now.Date;
            var offlineBefore = now.AddSeconds(-_options.NodeOfflineThresholdSeconds);
            var stalledBefore = now.AddMinutes(-_options.StalledSagaThresholdMinutes);

            var stateCounts = GroupCount(_context.Works, "StateName")
                .Select(x => new KeyValuePair<string, int>(x.Key, x.Value))
                .ToList();

            var queues = _context.Queues
                .Aggregate(PipelineDefinition<QueueDocument, BsonDocument>.Create(QueueStatPipeline))
                .ToList()
                .Select(x => new QueueStat(x["_id"].AsString, x["Depth"].AsInt32, x["Fetched"].AsInt32))
                .OrderByDescending(x => x.Depth)
                .ToList();

            var succeededToday = Count(_context.States,
                Builders<StateDocument>.Filter.And(
                    Builders<StateDocument>.Filter.Eq(t => t.Name, "succeeded"),
                    Builders<StateDocument>.Filter.Gte(t => t.CreateUtcTime, todayStart)));
            var failedToday = Count(_context.States,
                Builders<StateDocument>.Filter.And(
                    Builders<StateDocument>.Filter.Eq(t => t.Name, "failed"),
                    Builders<StateDocument>.Filter.Gte(t => t.CreateUtcTime, todayStart)));

            var nodes = _context.Servers.Find(FilterDefinition<ServerDocument>.Empty).ToList();
            var online = nodes.Count(n => n.LastHeartbeatUtcTime > offlineBefore);

            var sagaRunning = Count(_context.Sagas, Builders<SagaDocument>.Filter.Eq(t => t.Status, SagaStatus.Running));
            var sagaCompensating = Count(_context.Sagas, Builders<SagaDocument>.Filter.Eq(t => t.Status, SagaStatus.Compensating));
            var sagaStalled = Count(_context.Sagas, StalledFilter(stalledBefore));

            var recurringTotal = Count(_context.Recurrings, FilterDefinition<RecurringDocument>.Empty);
            var recurringPaused = Count(_context.Recurrings, Builders<RecurringDocument>.Filter.Eq(t => t.Paused, true));

            var batchTotal = Count(_context.Batches, FilterDefinition<BatchDocument>.Empty);
            var batchRunning = Count(_context.Batches, Builders<BatchDocument>.Filter.In(t => t.Status,
                new[] { BatchStatus.Awaiting, BatchStatus.Init, BatchStatus.Processing }));
            var batchFailure = Count(_context.Batches, Builders<BatchDocument>.Filter.Eq(t => t.Status, BatchStatus.Failure));

            var processing = stateCounts.FirstOrDefault(x => x.Key == "processing").Value;
            var enqueued = stateCounts.FirstOrDefault(x => x.Key == "enqueued").Value;

            // 字典索引：Key 不存在时 FirstOrDefault 给 default(KeyValuePair) → Key 为 null，Value 为 0
            double? failureRate = (succeededToday + failedToday) == 0
                ? (double?)null
                : Math.Round((double)failedToday / (succeededToday + failedToday), 4);

            return new OverviewView(
                OnlineNodes: online,
                RegisteredNodes: nodes.Count,
                StateCounts: stateCounts,
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
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var filters = new List<FilterDefinition<WorkDocument>>();
            if (!string.IsNullOrEmpty(state))
                filters.Add(Builders<WorkDocument>.Filter.Eq(t => t.StateName, state));
            if (!string.IsNullOrEmpty(name))
                filters.Add(Contains(Builders<WorkDocument>.Filter, t => t.Name, name!));
            // 支持按任务 ID 前缀/片段检索（完整 ID 亦可），便于从日志里粘一段 ID 直接定位
            if (!string.IsNullOrEmpty(workId))
                filters.Add(Contains(Builders<WorkDocument>.Filter, t => t.Id, workId!));
            if (!string.IsNullOrEmpty(queue))
            {
                // 队列在 Queue 集合上：先按队列名取出工作 Id，再回查工作（左连接语义的等价实现）
                var ids = _context.Queues
                    .Find(Builders<QueueDocument>.Filter.Eq(t => t.Queue, queue))
                    .Project(t => t.WorkId)
                    .ToList();
                filters.Add(Builders<WorkDocument>.Filter.In(t => t.Id, ids));
            }

            var filter = filters.Count == 0
                ? FilterDefinition<WorkDocument>.Empty
                : Builders<WorkDocument>.Filter.And(filters);

            var total = (int)Count(_context.Works, filter);
            var rows = _context.Works
                .Find(filter)
                .Sort(Builders<WorkDocument>.Sort.Descending(t => t.CreateUtcTime))
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToList();

            var fetched = QueueMap(rows.Select(t => t.Id).ToList());

            var items = rows.Select(w => new TaskListItem(
                w.Id, w.Name, w.StateName,
                fetched.TryGetValue(w.Id, out var q) ? q.Queue : null,
                NodeName(fetched.TryGetValue(w.Id, out var q2) ? q2.FetchedBy : null),
                w.CreateUtcTime,
                0)).ToList();

            return new PagedResult<TaskListItem>(items, total);
        }

        public TaskDetail? GetTaskDetail(string workId)
        {
            var work = _context.Works.Find(Builders<WorkDocument>.Filter.Eq(t => t.Id, workId)).FirstOrDefault();
            if (work == null) return null;

            var history = _context.States
                .Find(Builders<StateDocument>.Filter.Eq(t => t.WorkId, workId))
                .Sort(Builders<StateDocument>.Sort.Ascending(t => t.CreateUtcTime))
                .ToList()
                .Select(s => new StateHistoryItem(
                    s.Name, Redact(s.Info, _options) ?? string.Empty, s.CreateUtcTime))
                .ToList();

            var queue = _context.Queues
                .Find(Builders<QueueDocument>.Filter.Eq(t => t.WorkId, workId))
                .FirstOrDefault();

            return new TaskDetail(
                work.Id, work.Name, work.StateName,
                queue?.Queue, NodeName(queue?.FetchedBy),
                work.CreateUtcTime, work.ExpireUtcTime,
                Redact(work.InvocationJson, _options) ?? string.Empty,
                Redact(work.ParametersJson, _options) ?? string.Empty,
                history);
        }

        // ---------- 并发 ----------

        public ConcurrencyView GetConcurrency()
        {
            var now = DateTime.UtcNow;

            var works = _context.Works
                .Find(Builders<WorkDocument>.Filter.Eq(t => t.StateName, "processing"))
                .ToList();
            var ids = works.Select(w => w.Id).ToList();

            // 同一工作可能关联多条 processing 历史，取最早一条作为开始时间
            var starts = new Dictionary<string, DateTime>();
            if (ids.Count > 0)
            {
                var stateRows = _context.States
                    .Find(Builders<StateDocument>.Filter.And(
                        Builders<StateDocument>.Filter.In(t => t.WorkId, ids),
                        Builders<StateDocument>.Filter.Eq(t => t.Name, "processing")))
                    .ToList();

                foreach (var s in stateRows)
                {
                    if (!starts.TryGetValue(s.WorkId, out var cur) || s.CreateUtcTime < cur)
                        starts[s.WorkId] = s.CreateUtcTime;
                }
            }

            var fetched = QueueMap(ids);

            var processing = works.Select(w =>
            {
                var start = starts.TryGetValue(w.Id, out var t) ? (DateTime?)t : null;
                return new ProcessingItem(
                    w.Id, w.Name,
                    NodeName(fetched.TryGetValue(w.Id, out var q) ? q.FetchedBy : null),
                    Utc(start) ?? DateTime.MinValue,
                    start == null ? 0 : Math.Round((now - start.Value).TotalSeconds, 1));
            })
            .OrderByDescending(x => x.RunSeconds)
            .ToList();

            var queues = _context.Queues
                .Aggregate(PipelineDefinition<QueueDocument, BsonDocument>.Create(QueueStatPipeline))
                .ToList()
                .Select(x => new QueueStat(x["_id"].AsString, x["Depth"].AsInt32, x["Fetched"].AsInt32))
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
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var filters = new List<FilterDefinition<BatchDocument>>();
            if (!string.IsNullOrEmpty(status) && Enum.TryParse<BatchStatus>(status, true, out var parsed))
                filters.Add(Builders<BatchDocument>.Filter.Eq(t => t.Status, parsed));
            if (!string.IsNullOrEmpty(name))
                filters.Add(Contains(Builders<BatchDocument>.Filter, t => t.Name, name!));

            var filter = filters.Count == 0
                ? FilterDefinition<BatchDocument>.Empty
                : Builders<BatchDocument>.Filter.And(filters);

            var total = (int)Count(_context.Batches, filter);
            var rows = _context.Batches
                .Find(filter)
                .Sort(Builders<BatchDocument>.Sort.Descending(t => t.CreateUtcTime))
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToList();

            return new PagedResult<BatchListItem>(rows.Select(ToListItem).ToList(), total);
        }

        public BatchDetail? GetBatchDetail(string batchId)
        {
            var batch = _context.Batches
                .Find(Builders<BatchDocument>.Filter.Eq(t => t.BatchId, batchId))
                .FirstOrDefault();
            if (batch == null) return null;

            // 与 Sql 版同口径：按创建时间、再按名称排序（Mongo 侧 Id 是顺序 GUID，字符串序即生成序）
            var items = _context.BatchItems
                .Find(Builders<BatchItemDocument>.Filter.Eq(t => t.BatchId, batchId))
                .ToList()
                .OrderBy(i => i.CreateUtcTime).ThenBy(i => i.Name)
                .Select(i => new BatchItemRow(
                    i.Id, i.Name, i.BatchId, i.Status.ToString(), i.WorkId,
                    Redact(i.Message, _options) ?? string.Empty,
                    i.CreateUtcTime, i.UpdateUtcTime))
                .ToList();

            return new BatchDetail(ToListItem(batch), items);
        }

        // ---------- 周期任务 ----------

        public PagedResult<RecurringItem> QueryRecurrings(int page, int pageSize)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var total = (int)Count(_context.Recurrings, FilterDefinition<RecurringDocument>.Empty);
            var rows = _context.Recurrings
                .Find(FilterDefinition<RecurringDocument>.Empty)
                .Sort(Builders<RecurringDocument>.Sort.Ascending(t => t.Name))
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToList();

            return new PagedResult<RecurringItem>(rows.Select(ToItem).ToList(), total);
        }

        public PagedResult<TaskListItem> GetRecurringHistory(string recurringId, int page, int pageSize)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var recurring = _context.Recurrings
                .Find(Builders<RecurringDocument>.Filter.Eq(t => t.Id, recurringId))
                .FirstOrDefault();
            if (recurring == null) return new PagedResult<TaskListItem>(Array.Empty<TaskListItem>(), 0);

            // 调度器入队时会在工作参数里写入 {"RecurringId":"<id>"}，据此精确关联本次周期任务的历次执行。
            // 不能按 Work.Name 匹配：多个周期任务允许同名，按名匹配会把彼此的执行记录串在一起。
            var marker = "\"RecurringId\":\"" + recurringId + "\"";
            var filter = Contains(Builders<WorkDocument>.Filter, t => t.ParametersJson, marker);

            var total = (int)Count(_context.Works, filter);
            var rows = _context.Works
                .Find(filter)
                .Sort(Builders<WorkDocument>.Sort.Descending(t => t.CreateUtcTime))
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToList();

            var items = rows
                .Select(w => new TaskListItem(w.Id, w.Name, w.StateName, recurring.Queue, null, w.CreateUtcTime, 0))
                .ToList();

            return new PagedResult<TaskListItem>(items, total);
        }

        // ---------- Saga ----------

        public PagedResult<SagaListItem> QuerySagas(string? status, bool? stalledOnly, int page, int pageSize)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var stalledBefore = DateTime.UtcNow.AddMinutes(-_options.StalledSagaThresholdMinutes);

            var filters = new List<FilterDefinition<SagaDocument>>();
            if (!string.IsNullOrEmpty(status) && Enum.TryParse<SagaStatus>(status, true, out var parsed))
                filters.Add(Builders<SagaDocument>.Filter.Eq(t => t.Status, parsed));
            if (stalledOnly == true)
                filters.Add(StalledFilter(stalledBefore));

            var filter = filters.Count == 0
                ? FilterDefinition<SagaDocument>.Empty
                : Builders<SagaDocument>.Filter.And(filters);

            var total = (int)Count(_context.Sagas, filter);
            var rows = _context.Sagas
                .Find(filter)
                .Sort(Builders<SagaDocument>.Sort.Descending(t => t.CreateUtcTime))
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToList();

            // 卡住标记在内存中按阈值补算（阈值可配置，不适合写死进查询条件）
            var items = rows.Select(s => ToListItem(s, stalledBefore)).ToList();

            return new PagedResult<SagaListItem>(items, total);
        }

        public SagaDetail? GetSagaDetail(string sagaId)
        {
            var saga = _context.Sagas
                .Find(Builders<SagaDocument>.Filter.Eq(t => t.SagaId, sagaId))
                .FirstOrDefault();
            if (saga == null) return null;

            var stalledBefore = DateTime.UtcNow.AddMinutes(-_options.StalledSagaThresholdMinutes);

            var steps = _context.SagaSteps
                .Find(Builders<SagaStepDocument>.Filter.Eq(t => t.SagaId, sagaId))
                .Sort(Builders<SagaStepDocument>.Sort.Ascending(t => t.Index))
                .ToList()
                .Select(x => new SagaStepItem(
                    x.StepId, x.Index, x.Name, x.Status.ToString(),
                    Redact(x.Message, _options) ?? string.Empty,
                    x.ForwardWorkId, x.CompensateWorkId, x.OutputKey,
                    x.CreateUtcTime, x.UpdateUtcTime))
                .ToList();

            return new SagaDetail(
                ToListItem(saga, stalledBefore),
                Redact(saga.VariablesJson, _options) ?? string.Empty,
                Redact(saga.Message, _options) ?? string.Empty,
                saga.ExpireUtcTime,
                steps);
        }

        // ---------- 节点 ----------

        public IReadOnlyList<NodeItem> GetNodes()
        {
            var offlineBefore = DateTime.UtcNow.AddSeconds(-_options.NodeOfflineThresholdSeconds);

            var nodes = _context.Servers
                .Find(FilterDefinition<ServerDocument>.Empty)
                .Sort(Builders<ServerDocument>.Sort.Descending(t => t.LastHeartbeatUtcTime))
                .ToList();

            // 口径与并发视图一致：只算对应工作仍处于 processing 的队列行（抢占未释放 ≠ 正在执行）
            var processingIds = _context.Works
                .Find(Builders<WorkDocument>.Filter.Eq(t => t.StateName, "processing"))
                .Project(t => t.Id)
                .ToList();

            var processing = new Dictionary<string, int>();
            if (processingIds.Count > 0)
            {
                var rows = _context.Queues
                    .Find(Builders<QueueDocument>.Filter.And(
                        Builders<QueueDocument>.Filter.Ne(t => t.FetchedBy, null),
                        Builders<QueueDocument>.Filter.In(t => t.WorkId, processingIds)))
                    .Project(t => t.FetchedBy)
                    .ToList();

                foreach (var f in rows)
                {
                    var key = NodeName(f) ?? string.Empty;
                    processing[key] = processing.TryGetValue(key, out var c) ? c + 1 : 1;
                }
            }

            return nodes.Select(n =>
            {
                List<string> queues = new();
                try
                {
                    var data = Helper.Deserialize<ServerDataPayload>(n.JsonData ?? "{}");
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
            var work = await _context.Works
                .Find(Builders<WorkDocument>.Filter.Eq(t => t.Id, workId))
                .Project(t => t.StateName)
                .FirstOrDefaultAsync();

            return work;
        }

        public async Task<bool> RecurringExistsAsync(string recurringId)
        {
            var count = await _context.Recurrings
                .CountDocumentsAsync(Builders<RecurringDocument>.Filter.Eq(t => t.Id, recurringId));

            return count > 0;
        }

        public async Task<int> SetRecurringPausedAsync(string recurringId, bool paused)
        {
            var result = await _context.Recurrings.UpdateOneAsync(
                Builders<RecurringDocument>.Filter.Eq(t => t.Id, recurringId),
                Builders<RecurringDocument>.Update.Set(t => t.Paused, paused));

            // 用 MatchedCount（不是 ModifiedCount）：值未变化时 ModifiedCount=0 会被误判成"任务不存在"
            return (int)result.MatchedCount;
        }

        // ---------- 内部 ----------

        private BatchListItem ToListItem(BatchDocument b) => new(
            b.BatchId, b.Name, b.ParentBatchId, b.Status.ToString(),
            b.Count, b.DealCount, b.RetryCount,
            Redact(b.Message, _options) ?? string.Empty,
            b.CreateUtcTime, b.UpdateUtcTime, b.ExpireUtcTime);

        private static RecurringItem ToItem(RecurringDocument r) => new(
            r.Id, r.Name, r.Cron, r.Queue, r.TimeZone,
            r.Paused, r.NextExecutUtcTime, r.LastExecutUtcTime,
            r.LastWorkId, r.RetryCount, r.ErrorInfo);

        private static SagaListItem ToListItem(SagaDocument s, DateTime stalledBefore) => new(
            s.SagaId, s.Name, s.CorrelationId, s.Status.ToString(),
            s.StepCount, s.CurrentIndex, s.CompensationIndex,
            s.CreateUtcTime, s.UpdateUtcTime,
            s.Status == SagaStatus.Running && (s.UpdateUtcTime ?? s.CreateUtcTime) < stalledBefore);

        /// <summary>
        /// "卡住"判定：运行中且最后更新早于阈值。UpdateUtcTime 为空时退回创建时间。
        /// </summary>
        private static FilterDefinition<SagaDocument> StalledFilter(DateTime stalledBefore)
        {
            var running = Builders<SagaDocument>.Filter.Eq(t => t.Status, SagaStatus.Running);

            // (UpdateUtcTime ?? CreateUtcTime) < stalledBefore ⇒ 两个条件取或：
            // ① 有更新时间且早于阈值；② 无更新时间且创建时间早于阈值
            var updatedEarly = Builders<SagaDocument>.Filter.And(
                Builders<SagaDocument>.Filter.Ne(t => t.UpdateUtcTime, null),
                Builders<SagaDocument>.Filter.Lt(t => t.UpdateUtcTime, stalledBefore));
            var createdEarly = Builders<SagaDocument>.Filter.And(
                Builders<SagaDocument>.Filter.Eq(t => t.UpdateUtcTime, null),
                Builders<SagaDocument>.Filter.Lt(t => t.CreateUtcTime, stalledBefore));

            return Builders<SagaDocument>.Filter.And(running,
                Builders<SagaDocument>.Filter.Or(updatedEarly, createdEarly));
        }

        /// <summary>
        /// 队列统计管道：按队列名分组，Depth=消息总数，Fetched=已被取走的条数。
        /// FetchedBy 为空（含字段缺失）表示"未被取走"，用 $ne null 判定。
        /// </summary>
        private static readonly BsonDocument[] QueueStatPipeline =
        {
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", "$Queue" },
                { "Depth", new BsonDocument("$sum", 1) },
                { "Fetched", new BsonDocument("$sum", new BsonDocument("$cond",
                    new BsonArray { new BsonDocument("$ne", new BsonArray { "$FetchedBy", BsonNull.Value }), 1, 0 })) }
            })
        };

        /// <summary>按字段分组计数（等价于 SQL 的 GROUP BY + COUNT）。</summary>
        private static Dictionary<string, int> GroupCount<T>(IMongoCollection<T> collection, string field)
        {
            var pipeline = new[]
            {
                new BsonDocument("$group", new BsonDocument
                {
                    { "_id", "$" + field },
                    { "Cnt", new BsonDocument("$sum", 1) }
                })
            };

            return collection.Aggregate(PipelineDefinition<T, BsonDocument>.Create(pipeline))
                .ToList()
                .ToDictionary(x => x["_id"] == BsonNull.Value ? string.Empty : x["_id"].AsString, x => x["Cnt"].AsInt32);
        }

        private static int Count<T>(IMongoCollection<T> collection, FilterDefinition<T> filter)
            => (int)collection.CountDocuments(filter);

        /// <summary>字符串包含（大小写不敏感，与 SQLite 的 LIKE 默认行为一致）。</summary>
        private static FilterDefinition<T> Contains<T>(FilterDefinitionBuilder<T> builder,
            System.Linq.Expressions.Expression<Func<T, object>> field, string value)
            => builder.Regex(field, new BsonRegularExpression(Regex.Escape(value), "i"));

        /// <summary>取一批工作的队列行，用于还原队列名与执行节点。</summary>
        private Dictionary<string, QueueDocument> QueueMap(IReadOnlyList<string> workIds)
        {
            if (workIds.Count == 0) return new Dictionary<string, QueueDocument>();

            return _context.Queues
                .Find(Builders<QueueDocument>.Filter.In(t => t.WorkId, workIds))
                .ToList()
                .GroupBy(t => t.WorkId)
                .ToDictionary(g => g.Key, g => g.First());
        }

        private sealed class ServerDataPayload
        {
            public List<string> Queues { get; set; } = new();
        }
    }
}
