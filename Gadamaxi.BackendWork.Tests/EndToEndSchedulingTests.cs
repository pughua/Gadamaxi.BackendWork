using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Works;
using Gadamaxi.BackendWork.Works.States;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 端到端验证：启动真实后台服务（AddBackendWork 注册的 IHostedService / IWorkServer），
    /// 让框架自己的调度链路（队列消费、延时扫描、连续任务触发、批量展开、周期触发）
    /// 真正把任务跑完，验证"创建 → 调度 → 执行 → 状态落库"整条链路。
    /// 覆盖 WorkManagerExtention / IBatchManager / IRecurringWorkManager 的运行时行为。
    /// </summary>
    public class EndToEndSchedulingTests : BackendWorkTestBase
    {
        private const string CustomQueue = "e2e-queue";

        public EndToEndSchedulingTests() : base(deleteDatabaseOnDispose: false)
        {
            TestJobs.Reset();

            Option.ServerName = "e2e-test-server";
            // 自定义队列也要被消费，才能验证队列路由确实生效
            Option.Queues = new List<string> { Helper.DefaultQueue, CustomQueue };
            // 缩短轮询间隔让测试尽快推进，但不宜过密：每个轮询周期都会抢一次分布式锁并写库，
            // 线程越多、频率越高，SQLite 的写冲突越频繁
            Option.WorkPollingDelay = TimeSpan.FromMilliseconds(500);
            Option.SchedulePollingDelay = TimeSpan.FromMilliseconds(500);

            // 后台工作线程是常驻循环（WorkExecution 不响应停止令牌），测试结束后仍会空转。
            // 把周期与恢复进程调到极低频、卡死阈值调到极大，
            // 避免残留线程对后续用例产生干扰。
            Option.RecurringPollingDelay = TimeSpan.FromMinutes(10);
            Option.RecoveryPollingDelay = TimeSpan.FromMinutes(10);
            Option.WorkProcessingTimeout = TimeSpan.FromHours(10);
        }

        [Fact]
        public async Task 全链路_队列延时与连续任务_由真实调度执行()
        {
            var parentId = await Manager.Enqueue(() => TestJobs.Mark("parent"), "父任务");
            // 连续任务：父任务成功（Finish）后才由状态机触发入队
            var childId = await Manager.Awaiting(() => TestJobs.Mark("child"), parentId, "连续任务");
            // 延时任务：由 DelayedWorkProcess 到期投递
            var delayedId = await Manager.Schedule(() => TestJobs.MarkAsync("delayed"),
                TimeSpan.FromMilliseconds(100), "延时任务");
            // 指定队列的实例方法任务：验证队列路由与实例激活
            var customQueueId = await Manager.Enqueue<InstanceJobs>(j => j.Write("custom-queue"),
                "自定义队列任务", CustomQueue);

            var host = await StartHostedServer();
            try
            {
                Assert.True(await WaitUntil(() => AllSucceeded(parentId, childId, delayedId, customQueueId)),
                    "真实调度应在超时前执行完队列、延时与连续任务");
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
            }

            // 任务方法被真实执行，且每个任务只执行一次
            Assert.Equal(4, TestJobs.InvokeCount);
            Assert.Contains("parent", TestJobs.Records);
            Assert.Contains("child", TestJobs.Records);
            Assert.Contains("delayed", TestJobs.Records);
            Assert.Contains("custom-queue", TestJobs.Records);

            // 连续任务必须在父任务之后执行
            var records = TestJobs.Records.ToList();
            Assert.True(records.IndexOf("parent") < records.IndexOf("child"),
                "连续任务应在父任务完成后才执行");

            // 消息确认后出队，且任务名全程可读
            foreach (var pair in new[]
            {
                (parentId, "父任务"), (childId, "连续任务"), (delayedId, "延时任务"), (customQueueId, "自定义队列任务")
            })
            {
                Assert.Equal(SucceededWorkState.Name, (await Query.GetLastState(pair.Item1))!.StateName);
                // 消息在状态落库后才异步删除，这里要给确认出队留出时间
                Assert.True(await WaitUntil(async () => !await QueueRowExists(pair.Item1), TimeSpan.FromSeconds(10)),
                    $"{pair.Item2} 执行完成后消息应出队");
                Assert.Equal(pair.Item2, await WorkNameInDb(pair.Item1));
            }

            // 自定义队列：负载内容保留队列名，重试/重投才不会漂移到默认队列
            var customData = await Query.GetWorkData(customQueueId);
            Assert.NotNull(customData);
            Assert.Equal(CustomQueue, customData!.Content.Queue);
        }

        [Fact]
        public async Task 全链路_批量与周期任务_由真实调度执行()
        {
            string? firstItemId = null;
            var batchId = await Provider.GetRequiredService<IBatchManager>().Create(ctx =>
            {
                firstItemId = ctx.Enqueue(() => TestJobs.Mark("batch-1"));
                ctx.Enqueue<InstanceJobs>(j => j.Write("batch-2"));
                ctx.Enqueue(() => TestJobs.MarkAsync("batch-3"));
                // 批量内的连续任务：等 batch-1 对应的子工作完成后才执行
                ctx.Awaiting(() => TestJobs.Mark("batch-4"), firstItemId);
            }, "批量E2E");

            var recurringManager = Provider.GetRequiredService<IRecurringWorkManager>();
            await recurringManager.AddOrUpdate("e2e-recurring", () => TestJobs.Mark("recurring"),
                "* * * * *", Helper.DefaultQueue,
                new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "周期E2E");

            // 周期进程首次触发最长需等待 1 分钟（对齐整分），这里直接 Trigger 触发，
            // 触发后工作的执行仍完全交给真实调度链路
            await recurringManager.Trigger("e2e-recurring");
            var recurringWorkId = (await Query.GetRecurringWorkById("e2e-recurring"))!.LastWorkId;
            Assert.False(string.IsNullOrEmpty(recurringWorkId));

            var server = Provider.GetRequiredService<IWorkServer>();
            var runTask = server.Run();
            try
            {
                var ok = await WaitUntil(async () =>
                {
                    var batch = await Query.GetBatchById(batchId);
                    if (batch == null || batch.Status != BatchStatus.Success)
                    {
                        return false;
                    }

                    var items = await Query.GetBatchItems(batchId, 0, 10);
                    if (items.Count != 4 || items.Any(t => string.IsNullOrEmpty(t.WorkId)))
                    {
                        return false;
                    }

                    return await AllSucceeded(items.Select(t => t.WorkId!).Append(recurringWorkId).ToArray());
                }, TimeSpan.FromSeconds(60));

                if (!ok)
                {
                    Assert.Fail(await DumpBatchDiagnostics(batchId, recurringWorkId));
                }
            }
            finally
            {
                await server.Stop();
            }

            if (runTask.IsFaulted)
            {
                throw runTask.Exception!.InnerExceptions[0];
            }

            // 批量展开：子工作由 BatchWorkProcess 创建，名称与批量项一致，且批量项可回溯子工作
            var items2 = await Query.GetBatchItems(batchId, 0, 10);
            var workNames = new List<string>();
            foreach (var item in items2)
            {
                Assert.False(string.IsNullOrEmpty(item.WorkId), $"{item.Name} 应记录子工作 Id");
                workNames.Add(await WorkNameInDb(item.WorkId!));
            }
            Assert.Equal(new[] { "批量E2E#1", "批量E2E#2", "批量E2E#3", "批量E2E#4" }, workNames.OrderBy(t => t).ToArray());

            // 子任务全部被真实执行，且批量内的连续任务排在父任务之后
            var records = TestJobs.Records.ToList();
            foreach (var marker in new[] { "batch-1", "batch-2", "batch-3", "batch-4" })
            {
                Assert.Contains(marker, records);
            }
            Assert.True(records.IndexOf("batch-1") < records.IndexOf("batch-4"),
                "批量内的连续任务应在其父子工作完成后才执行");

            // 周期任务触发的工作：继承周期任务名并成功执行
            Assert.Equal("周期E2E", await WorkNameInDb(recurringWorkId));
            Assert.Equal(SucceededWorkState.Name, (await Query.GetLastState(recurringWorkId))!.StateName);
            Assert.Contains("recurring", TestJobs.Records);
        }

        #region helpers

        /// <summary>失败时输出批量/子任务/周期任务的当前状态，便于定位链路断点</summary>
        private async Task<string> DumpBatchDiagnostics(string batchId, string recurringWorkId)
        {
            var batch = await Query.GetBatchById(batchId);
            var items = await Query.GetBatchItems(batchId, 0, 10);

            var itemDetails = new List<string>();
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.WorkId))
                {
                    itemDetails.Add($"{item.Name}({item.Status},no-work)");
                    continue;
                }

                var state = (await Query.GetLastState(item.WorkId))?.StateName ?? "no-state";
                var chain = await StateChain(item.WorkId);
                var parentId = await AwaitingParentId(item.WorkId);
                itemDetails.Add($"{item.Name}({item.Status},{state},[{chain}],parent={parentId},msg={item.Message})");
            }

            var recurringState = (await Query.GetLastState(recurringWorkId))?.StateName ?? "no-state";

            var rows = await Db.GetAllQueueRowsAsync();
            var queueRows = rows
                .Select(t => $"{t.WorkId}@{t.Queue}#{(t.FetchUtcTime == null ? "未取走" : "已取走")}")
                .ToList();

            return $"批量状态={batch?.Status}，批量项=[{string.Join(", ", itemDetails)}]，"
                + $"周期任务状态={recurringState}，队列消息=[{string.Join(", ", queueRows)}]";
        }

        private async Task<string> StateChain(string workId)
        {
            var names = await Db.GetStateChainAsync(workId);

            return string.Join(" > ", names);
        }

        private async Task<string> AwaitingParentId(string workId)
        {
            var data = await Db.GetStateDataAsync(workId, AwaitingWorkState.Name);

            return data ?? "-";
        }

        /// <summary>
        /// 所有指定工作都已成功完成。
        /// 用"存在 succeeded 状态记录"判定而不是"最后一条状态"，
        /// 避免同一毫秒内写入多条状态时排序不稳定导致的误判。
        /// </summary>
        private async Task<bool> AllSucceeded(params string[] workIds)
        {
            foreach (var workId in workIds)
            {
                if (await Query.CountState(workId, SucceededWorkState.Name) <= 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 走生产注册路径启动后台服务：AddBackendWork 注册 IHostedService（ServerBackgroundService）。
        /// </summary>
        private async Task<IHostedService> StartHostedServer()
        {
            var host = Provider.GetServices<IHostedService>().First();
            await host.StartAsync(CancellationToken.None);
            return host;
        }

        #endregion
    }
}
