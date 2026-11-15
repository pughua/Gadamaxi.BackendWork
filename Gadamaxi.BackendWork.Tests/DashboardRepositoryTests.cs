using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Dashboards;
using Gadamaxi.BackendWork.Sagas;
using Gadamaxi.BackendWork.Sql;
using Gadamaxi.BackendWork.Sql.Tables;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Gadamaxi.BackendWork.Dashboards.DashboardModels;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// Dashboard 仓储（SqlDashboardRepository，原独立项目 Gadamaxi.BackendWork.Sql.Dashboard）的单元测试。
    /// 直接用 FluentMigrator 建出的真实 schema 播种数据，验证面板各视图的查询口径。
    /// </summary>
    public class DashboardOverviewTests : BackendWorkTestBase
    {
        private SqlDashboardRepository Repo(BackendWorkDashboardOptions? options = null)
            => new(Provider.GetRequiredService<DataOptions>(), options ?? new BackendWorkDashboardOptions());

        [SqlBackendOnlyFact]
        public void 总览_各维度统计口径正确()
        {
            var now = Helper.CurrentUtcTime();
            using (var db = OpenDb())
            {
                // 工作：2 处理中 + 1 排队 + 1 成功
                DashboardSeed.Work(db, "w-p1", "处理一", "processing", now.AddMinutes(-5));
                DashboardSeed.Work(db, "w-p2", "处理二", "processing", now.AddMinutes(-4));
                DashboardSeed.Work(db, "w-e1", "排队一", "enqueued", now.AddMinutes(-3));
                DashboardSeed.Work(db, "w-s1", "成功一", "succeeded", now.AddMinutes(-2));

                // 状态历史：今天 3 成功 + 1 失败 → 失败率 0.25
                DashboardSeed.State(db, "st1", "succeeded", "w-s1", now);
                DashboardSeed.State(db, "st2", "succeeded", "w-p1", now.AddMinutes(-1));
                DashboardSeed.State(db, "st3", "succeeded", "w-e1", now.AddMinutes(-2));
                DashboardSeed.State(db, "st4", "failed", "w-p2", now);

                // 队列：default 2 条（1 条已被抢占）、critical 1 条
                DashboardSeed.Queue(db, 1, "w-p1", "default", "node-a#fetch-1");
                DashboardSeed.Queue(db, 2, "w-e1", "default", null);
                DashboardSeed.Queue(db, 3, "w-x1", "critical", null);

                // 节点：1 在线（刚刚心跳）+ 1 失联（2 分钟前心跳，阈值 60 秒）
                DashboardSeed.Server(db, "srv-1", now, now);
                DashboardSeed.Server(db, "srv-2", now.AddDays(-1), now.AddMinutes(-2));

                // Saga：1 运行中（新鲜）+ 1 运行中（停滞 60 分钟）+ 1 补偿中
                DashboardSeed.Saga(db, "sg1", "订单 Saga", SagaStatus.Running, 3, now, now);
                DashboardSeed.Saga(db, "sg2", "停滞 Saga", SagaStatus.Running, 2, now.AddHours(-2), now.AddMinutes(-60));
                DashboardSeed.Saga(db, "sg3", "补偿 Saga", SagaStatus.Compensating, 2, now, now);

                // 周期：2 条，1 条暂停
                DashboardSeed.Recurring(db, "r1", "日报", "0 8 * * *", paused: false);
                DashboardSeed.Recurring(db, "r2", "清理", "*/5 * * * *", paused: true);

                // 批量：1 处理中 + 1 失败 + 1 成功
                DashboardSeed.Batch(db, "b1", "导入批次", BatchStatus.Processing, now);
                DashboardSeed.Batch(db, "b2", "失败批次", BatchStatus.Failure, now);
                DashboardSeed.Batch(db, "b3", "成功批次", BatchStatus.Success, now);
            }

            var view = Repo().GetOverview();

            Assert.Equal(1, view.OnlineNodes);
            Assert.Equal(2, view.RegisteredNodes);
            Assert.Equal(2, view.ProcessingTotal);
            Assert.Equal(1, view.EnqueuedTotal);
            Assert.Equal(3, view.SucceededToday);
            Assert.Equal(1, view.FailedToday);
            Assert.Equal(0.25, view.FailureRateToday);
            Assert.Equal(2, view.Queues.Count);
            Assert.Equal("default", view.Queues[0].Queue); // 按深度降序
            Assert.Equal(2, view.Queues[0].Depth);
            Assert.Equal(1, view.Queues[0].Fetched);
            Assert.Equal(1, view.Queues[1].Depth);
            Assert.Equal(2, view.SagaRunning);
            Assert.Equal(1, view.SagaCompensating);
            Assert.Equal(1, view.SagaStalled);
            Assert.Equal(2, view.RecurringTotal);
            Assert.Equal(1, view.RecurringPaused);
            Assert.Equal(3, view.BatchTotal);
            Assert.Equal(1, view.BatchRunning);
            Assert.Equal(1, view.BatchFailure);
        }

        [SqlBackendOnlyFact]
        public void 总览_无今日状态时失败率为null()
        {
            var view = Repo().GetOverview();
            Assert.Null(view.FailureRateToday);
            Assert.Equal(0, view.SucceededToday);
            Assert.Equal(0, view.FailedToday);
            Assert.Equal(0, view.RegisteredNodes);
        }
    }

    public class DashboardQueryTests : BackendWorkTestBase
    {
        private readonly DateTime _now = Helper.CurrentUtcTime();

        private SqlDashboardRepository Repo(BackendWorkDashboardOptions? options = null)
            => new(Provider.GetRequiredService<DataOptions>(), options ?? new BackendWorkDashboardOptions());

        [SqlBackendOnlyFact]
        public void 任务分页_支持状态名称队列与ID片段过滤_并还原节点名()
        {
            using (var db = OpenDb())
            {
                DashboardSeed.Work(db, "w-1", "同步订单", "processing", _now.AddMinutes(-3), parametersJson: "{\"Source\":\"api\"}");
                DashboardSeed.Work(db, "w-2", "同步库存", "enqueued", _now.AddMinutes(-2));
                DashboardSeed.Work(db, "w-3", "发邮件", "enqueued", _now.AddMinutes(-1));
                DashboardSeed.Queue(db, 1, "w-1", "default", "node-a#fetch-9");
                DashboardSeed.Queue(db, 2, "w-3", "mail", null);
            }

            // 无过滤
            var all = Repo().QueryTasks(null, null, null, null, 1, 10);
            Assert.Equal(3, all.Total);
            Assert.Equal("w-3", all.Items[0].Id); // 按创建时间降序
            // 节点名还原：去掉 #抢占标记 后缀
            Assert.Equal("node-a", all.Items.First(x => x.Id == "w-1").Node);
            Assert.Equal("default", all.Items.First(x => x.Id == "w-1").Queue);
            // 无队列行的工作：队列与节点为空
            Assert.Null(all.Items.First(x => x.Id == "w-2").Queue);

            // 按状态过滤
            Assert.Equal(2, Repo().QueryTasks("enqueued", null, null, null, 1, 10).Total);
            // 按名称模糊过滤
            Assert.Equal(2, Repo().QueryTasks(null, "同步", null, null, 1, 10).Total);
            // 按队列过滤
            Assert.Equal("w-3", Repo().QueryTasks(null, null, "mail", null, 1, 10).Items.Single().Id);
            // 按 ID 片段过滤
            Assert.Equal("w-2", Repo().QueryTasks(null, null, null, "w-2", 1, 10).Items.Single().Id);

            // 分页：第 2 页、每页 2 条 → 只剩 1 条
            var page2 = Repo().QueryTasks(null, null, null, null, 2, 2);
            Assert.Equal(3, page2.Total);
            Assert.Single(page2.Items);
        }

        [SqlBackendOnlyFact]
        public void 任务详情_返回状态历史并按配置脱敏_未找到返回null()
        {
            var longJson = "{\"arg\":\"" + new string('x', 50) + "\"}";
            using (var db = OpenDb())
            {
                DashboardSeed.Work(db, "w-1", "同步订单", "processing", _now,
                    invocationJson: longJson);
                DashboardSeed.State(db, "st1", "enqueued", "w-1", _now.AddMinutes(-2));
                DashboardSeed.State(db, "st2", "processing", "w-1", _now.AddMinutes(-1));
                DashboardSeed.Queue(db, 1, "w-1", "default", "node-a#fetch-1");
            }

            var options = new BackendWorkDashboardOptions { RedactSensitiveData = true, RedactMaxLength = 20 };
            var detail = Repo(options).GetTaskDetail("w-1");

            Assert.NotNull(detail);
            Assert.Equal("w-1", detail!.Id);
            Assert.Equal("node-a", detail.Node);
            Assert.EndsWith("…(已截断)", detail.InvocationJson);
            Assert.True(detail.InvocationJson.Length < longJson.Length);
            // 历史按时间升序
            Assert.Equal(2, detail.StateHistory.Count);
            Assert.Equal("enqueued", detail.StateHistory[0].Name);
            Assert.Equal("processing", detail.StateHistory[1].Name);

            Assert.Null(Repo().GetTaskDetail("不存在"));
        }

        [SqlBackendOnlyFact]
        public void 并发视图_统计处理中任务慢任务与节点维度()
        {
            var options = new BackendWorkDashboardOptions { SlowTaskThresholdSeconds = 60 };
            using (var db = OpenDb())
            {
                // 慢任务：processing 已持续 10 分钟，同一工作有两条 processing 历史 → 取最早一条
                DashboardSeed.Work(db, "w-slow", "慢任务", "processing", _now.AddMinutes(-10));
                DashboardSeed.Queue(db, 1, "w-slow", "default", "node-a#fetch-1");
                DashboardSeed.State(db, "st1", "processing", "w-slow", _now.AddMinutes(-10));
                DashboardSeed.State(db, "st2", "processing", "w-slow", _now.AddMinutes(-5));

                // 新任务：无状态历史 → 开始时间空、不记慢
                DashboardSeed.Work(db, "w-new", "新任务", "processing", _now);
                DashboardSeed.Queue(db, 2, "w-new", "default", "node-b#fetch-2");
            }

            var view = Repo(options).GetConcurrency();

            Assert.Equal(2, view.ProcessingTotal);
            Assert.Equal(1, view.SlowTotal);
            var slow = view.Processing.First(x => x.WorkId == "w-slow");
            Assert.Equal("node-a", slow.Node);
            Assert.True(slow.RunSeconds >= 600);
            var fresh = view.Processing.First(x => x.WorkId == "w-new");
            Assert.Equal(0, fresh.RunSeconds);
            // 节点维度聚合
            Assert.Equal(2, view.Nodes.Count);
            Assert.Contains(view.Nodes, n => n.Node == "node-a" && n.Processing == 1);
            Assert.Contains(view.Nodes, n => n.Node == "node-b" && n.Processing == 1);
        }

        [SqlBackendOnlyFact]
        public void 批量分页_按状态与名称过滤_详情含子任务列表()
        {
            using (var db = OpenDb())
            {
                DashboardSeed.Batch(db, "b1", "导入-九月", BatchStatus.Processing, _now, count: 3, dealCount: 1);
                DashboardSeed.Batch(db, "b2", "导入-八月", BatchStatus.Failure, _now.AddDays(-1));
                DashboardSeed.BatchItem(db, "bi-1", "b1", "行1", BatchStatus.Success, workId: "w-1");
                DashboardSeed.BatchItem(db, "bi-2", "b1", "行2", BatchStatus.Processing, workId: "w-2");
            }

            Assert.Equal(2, Repo().QueryBatches(null, null, 1, 10).Total);
            Assert.Equal(1, Repo().QueryBatches("Processing", null, 1, 10).Total);
            Assert.Equal(1, Repo().QueryBatches(null, "八月", 1, 10).Total);

            var detail = Repo().GetBatchDetail("b1");
            Assert.NotNull(detail);
            Assert.Equal(3, detail!.Batch.Count);
            Assert.Equal(2, detail.Items.Count);
            Assert.Equal("行1", detail.Items[0].Name); // 按创建时间+名称排序
            Assert.Equal("w-2", detail.Items[1].WorkId);

            Assert.Null(Repo().GetBatchDetail("不存在"));
        }

        [SqlBackendOnlyFact]
        public void 周期历史_按参数中的RecurringId关联_同名周期任务不串()
        {
            using (var db = OpenDb())
            {
                // 两个周期任务同名（业务上允许），仅靠任务名无法区分历史
                DashboardSeed.Recurring(db, "r1", "日报", "0 8 * * *", paused: false, queue: "mail");
                DashboardSeed.Recurring(db, "r2", "日报", "0 9 * * *", paused: false);
                // 各自的执行历史：调度器把 RecurringId 写进工作参数
                DashboardSeed.Work(db, "w-r1a", "日报", "succeeded", _now.AddMinutes(-30),
                    parametersJson: "{\"RecurringId\":\"r1\"}");
                DashboardSeed.Work(db, "w-r1b", "日报", "succeeded", _now.AddMinutes(-10),
                    parametersJson: "{\"RecurringId\":\"r1\"}");
                DashboardSeed.Work(db, "w-r2a", "日报", "failed", _now.AddMinutes(-20),
                    parametersJson: "{\"RecurringId\":\"r2\"}");
                // 干扰项：不含标记的普通任务
                DashboardSeed.Work(db, "w-plain", "日报", "succeeded", _now, parametersJson: "{}");
            }

            var recurrings = Repo().QueryRecurrings(1, 10);
            Assert.Equal(2, recurrings.Total);

            var history1 = Repo().GetRecurringHistory("r1", 1, 10);
            Assert.Equal(2, history1.Total);
            Assert.Equal("w-r1b", history1.Items[0].Id); // 降序
            Assert.Equal("mail", history1.Items[0].Queue); // 队列取自周期任务定义

            Assert.Equal(1, Repo().GetRecurringHistory("r2", 1, 10).Total);
            // 不存在的周期任务：空结果而非抛异常
            var missing = Repo().GetRecurringHistory("不存在", 1, 10);
            Assert.Equal(0, missing.Total);
            Assert.Empty(missing.Items);
        }

        [SqlBackendOnlyFact]
        public void Saga分页_卡住标记与stalledOnly过滤_详情含步骤时间线()
        {
            var options = new BackendWorkDashboardOptions { StalledSagaThresholdMinutes = 30, RedactMaxLength = 20 };
            using (var db = OpenDb())
            {
                DashboardSeed.Saga(db, "sg1", "新鲜 Saga", SagaStatus.Running, 2, _now, _now);
                DashboardSeed.Saga(db, "sg2", "停滞 Saga", SagaStatus.Running, 3, _now.AddHours(-2), _now.AddMinutes(-60));
                DashboardSeed.Saga(db, "sg3", "已完成", SagaStatus.Completed, 2, _now.AddDays(-1), _now.AddDays(-1));
                DashboardSeed.SagaStep(db, "step-1", "sg2", 0, "扣库存", SagaStepStatus.Succeeded);
                DashboardSeed.SagaStep(db, "step-2", "sg2", 1, "扣款", SagaStepStatus.Running, message: new string('e', 100));
            }

            var all = Repo(options).QuerySagas(null, null, 1, 10);
            Assert.Equal(3, all.Total);
            Assert.False(all.Items.First(x => x.SagaId == "sg1").Stalled);
            Assert.True(all.Items.First(x => x.SagaId == "sg2").Stalled);
            Assert.False(all.Items.First(x => x.SagaId == "sg3").Stalled); // 非Running不算卡住

            var stalledOnly = Repo(options).QuerySagas(null, stalledOnly: true, page: 1, pageSize: 10);
            Assert.Equal("sg2", stalledOnly.Items.Single().SagaId);

            var byStatus = Repo(options).QuerySagas("completed", null, 1, 10);
            Assert.Equal("sg3", byStatus.Items.Single().SagaId);

            var detail = Repo(options).GetSagaDetail("sg2");
            Assert.NotNull(detail);
            Assert.True(detail!.Saga.Stalled);
            Assert.Equal(2, detail.Steps.Count);
            Assert.Equal("扣库存", detail.Steps[0].Name); // 按 Index 排序
            Assert.EndsWith("…(已截断)", detail.Steps[1].Message); // 步骤消息同样脱敏

            Assert.Null(Repo().GetSagaDetail("不存在"));
        }

        [SqlBackendOnlyFact]
        public void 节点列表_在线判定队列解析与处理中计数()
        {
            using (var db = OpenDb())
            {
                DashboardSeed.Server(db, "srv-1", _now.AddDays(-1), _now, jsonData: "{\"Queues\":[\"default\",\"critical\"]}");
                DashboardSeed.Server(db, "srv-2", _now.AddDays(-1), _now.AddMinutes(-2), jsonData: "不是JSON");
                DashboardSeed.Work(db, "w-1", "任务", "processing", _now);
                DashboardSeed.Queue(db, 1, "w-1", "default", "srv-1#fetch-1");
            }

            var nodes = Repo().GetNodes();
            Assert.Equal(2, nodes.Count);
            // 按心跳降序
            Assert.Equal("srv-1", nodes[0].ServerName);
            Assert.True(nodes[0].Online);
            Assert.Equal(new List<string> { "default", "critical" }, nodes[0].Queues.ToList());
            Assert.Equal(1, nodes[0].Processing); // processing 工作 + 被抢占的队列行
            Assert.False(nodes[1].Online);
            Assert.Empty(nodes[1].Queues); // JSON 解析失败不影响展示
            Assert.Equal(0, nodes[1].Processing);

            Assert.Equal("srv-1", Repo().GetNode("srv-1")!.ServerName);
            Assert.Null(Repo().GetNode("不存在"));
        }

        [SqlBackendOnlyFact]
        public async Task 写操作支撑_状态读取_存在性判断与暂停切换()
        {
            using (var db = OpenDb())
            {
                DashboardSeed.Work(db, "w-1", "任务", "processing", _now);
                DashboardSeed.Recurring(db, "r1", "日报", "0 8 * * *", paused: false);
            }

            Assert.Equal("processing", await Repo().GetWorkStateAsync("w-1"));
            Assert.Null(await Repo().GetWorkStateAsync("不存在"));
            Assert.True(await Repo().RecurringExistsAsync("r1"));
            Assert.False(await Repo().RecurringExistsAsync("不存在"));

            Assert.Equal(1, await Repo().SetRecurringPausedAsync("r1", true));
            using (var db = OpenDb())
            {
                Assert.True(db.RecurringTable.First(r => r.Id == "r1").Paused);
            }
        }
    }

    /// <summary>Dashboard 测试的数据库播种工具：直接写底层表，精确控制时间与关联字段。</summary>
    internal static class DashboardSeed
    {
        public static void Work(DBConnection db, string id, string name, string state, DateTime created,
            string parametersJson = "", string invocationJson = "{}")
            => db.Insert(new WorkTable
            {
                Id = id,
                Name = name,
                StateId = id + "-current",
                StateName = state,
                InvocationJson = invocationJson,
                ParametersJson = parametersJson,
                CreateUtcTime = created,
            });

        public static void State(DBConnection db, string id, string name, string workId, DateTime created, string info = "")
            => db.Insert(new StateTable
            {
                Id = id,
                Name = name,
                WorkId = workId,
                Info = info,
                CreateUtcTime = created,
                JsonData = "{}",
            });

        public static void Queue(DBConnection db, long id, string workId, string queue, string? fetchedBy)
        {
            // 用列级插入而非 db.Insert(实体)：SqlServer 的 Queue.Id 是 IDENTITY，
            // 显式赋值会报 "Cannot insert explicit value for identity column"，
            // 故那里省略 Id 交给自增（写入顺序即自增顺序，断言不依赖具体 Id 值）；
            // SQLite 的 INTEGER PRIMARY KEY 允许显式赋值，保持原样以便测试精确控制。
            var insert = db.QueueTable
                .Value(t => t.WorkId, workId)
                .Value(t => t.Queue, queue)
                .Value(t => t.FetchedBy, fetchedBy)
                .Value(t => t.FetchUtcTime, fetchedBy == null ? (DateTime?)null : Helper.CurrentUtcTime());

            if (db.DataProvider == null
                || !db.DataProvider.Name.StartsWith(ProviderName.SqlServer, StringComparison.OrdinalIgnoreCase))
            {
                insert = insert.Value(t => t.Id, id);
            }

            insert.Insert();
        }

        public static void Server(DBConnection db, string name, DateTime created, DateTime heartbeat, string jsonData = "{}")
            => db.Insert(new ServerTable
            {
                ServerName = name,
                CreateUtcTime = created,
                LastHeartbeatUtcTime = heartbeat,
                JsonData = jsonData,
            });

        public static void Recurring(DBConnection db, string id, string name, string cron, bool paused,
            string queue = "default")
        {
            db.Insert(new RecurringTable
            {
                Id = id,
                Name = name,
                Cron = cron,
                Queue = queue,
                TimeZone = "UTC",
                JsonContent = "{}",
                CreateUtcTime = Helper.CurrentUtcTime(),
                Paused = paused,
            });
        }

        public static void Batch(DBConnection db, string batchId, string name, BatchStatus status, DateTime created,
            int count = 0, int dealCount = 0)
            => db.Insert(new BatchTable
            {
                BatchId = batchId,
                Name = name,
                ParentBatchId = string.Empty,
                Status = status,
                Count = count,
                DealCount = dealCount,
                Message = string.Empty,
                CreateUtcTime = created,
                UpdateUtcTime = created,
                ExpireUtcTime = created.AddDays(7),
            });

        public static void BatchItem(DBConnection db, string id, string batchId, string name, BatchStatus status,
            string? workId = null, string message = "", DateTime? created = null)
            => db.Insert(new BatchItemTable
            {
                Id = id,
                Name = name,
                BatchId = batchId,
                Status = status,
                WorkId = workId,
                Message = message,
                WorkJson = "{}",
                DataJson = "{}",
                DataType = string.Empty,
                CreateUtcTime = created ?? Helper.CurrentUtcTime(),
                UpdateUtcTime = created ?? Helper.CurrentUtcTime(),
            });

        public static void Saga(DBConnection db, string sagaId, string name, SagaStatus status, int stepCount,
            DateTime created, DateTime? updated, string variablesJson = "{}")
            => db.Insert(new SagaTable
            {
                SagaId = sagaId,
                Name = name,
                CorrelationId = sagaId + "-corr",
                SagaType = "TestSaga",
                InputJson = "{}",
                Status = status,
                StepCount = stepCount,
                CurrentIndex = 0,
                CompensationIndex = 0,
                VariablesJson = variablesJson,
                Message = string.Empty,
                ExpireUtcTime = created.AddDays(1),
                CreateUtcTime = created,
                UpdateUtcTime = updated ?? created,
            });

        public static void SagaStep(DBConnection db, string stepId, string sagaId, int index, string name,
            SagaStepStatus status, string message = "")
            => db.Insert(new SagaStepTable
            {
                StepId = stepId,
                SagaId = sagaId,
                Index = index,
                Name = name,
                Status = status,
                Message = message,
                ForwardMethod = name,
                CompensateMethod = "Undo" + name,
                CreateUtcTime = Helper.CurrentUtcTime(),
                UpdateUtcTime = Helper.CurrentUtcTime(),
            });
    }
}
