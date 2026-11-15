using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works;
using Gadamaxi.BackendWork.Works.States;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 任务名（Work.Name / Batch.Name / BatchItem.Name）落库与传播规则验证。
    /// 名称用于监控面板人工识别任务，因此是必填项：
    /// 空名 / 超长名在入口直接抛错，批量与周期任务按各自规则派生名称。
    /// </summary>
    public class WorkNameTests : BackendWorkTestBase
    {
        [Fact]
        public async Task 创建任务_名称落库_并可读回()
        {
            var workId = await Manager.Enqueue(() => TestJobs.StaticWork(), "对账单生成", Helper.DefaultQueue);

            Assert.Equal("对账单生成", await WorkNameInDb(workId));

            var data = await Query.GetWorkData(workId);
            Assert.NotNull(data);
            Assert.Equal("对账单生成", data!.Name);
        }

        [Fact]
        public async Task 延时任务_名称落库()
        {
            var workId = await Manager.Schedule(() => TestJobs.StaticWork(), TimeSpan.FromMinutes(5), "延时对账");

            Assert.Equal("延时对账", await WorkNameInDb(workId));
        }

        [Fact]
        public async Task 后续任务_名称落库()
        {
            var parentId = await Manager.Enqueue(() => TestJobs.StaticWork(), "前序任务", Helper.DefaultQueue);
            var childId = await Manager.Awaiting(() => TestJobs.StaticWork(), parentId, "后置清理");

            Assert.Equal("后置清理", await WorkNameInDb(childId));
        }

        [Fact]
        public async Task 空名_创建失败()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => Manager.Enqueue(() => TestJobs.StaticWork(), string.Empty, Helper.DefaultQueue));

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => Manager.Schedule(() => TestJobs.StaticWork(), TimeSpan.FromMinutes(1), string.Empty));

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => Manager.Awaiting(() => TestJobs.StaticWork(), "parent", string.Empty));
        }

        [Fact]
        public async Task 超长名_创建失败()
        {
            var tooLong = new string('x', Helper.MaxWorkNameLength + 1);

            await Assert.ThrowsAsync<ArgumentException>(
                () => Manager.Enqueue(() => TestJobs.StaticWork(), tooLong, Helper.DefaultQueue));
        }

        [Fact]
        public async Task 批量任务_批量名与子任务名都落库()
        {
            var batchId = await CreateBatch("月结对账", 3);

            Assert.Equal("月结对账", await BatchNameInDb(batchId));

            var itemNames = await BatchItemNamesInDb(batchId);
            Assert.Equal(new[] { "月结对账#1", "月结对账#2", "月结对账#3" }, itemNames.OrderBy(t => t));

            // 读取接口同样带出名称
            var batch = await Query.GetBatchById(batchId);
            Assert.NotNull(batch);
            Assert.Equal("月结对账", batch!.Name);

            var full = await Query.GetFullBatchById(batchId);
            Assert.NotNull(full);
            Assert.Equal("月结对账", full!.Name);
            Assert.Equal(3, full.Items.Count);
            Assert.All(full.Items, t => Assert.StartsWith("月结对账#", t.Name));

            var items = await Query.GetBatchItems(batchId, 0, 10);
            Assert.All(items, t => Assert.False(string.IsNullOrEmpty(t.Name)));
        }

        [Fact]
        public async Task 批量名超过批量子任务预留长度_创建失败()
        {
            var tooLong = new string('x', Helper.MaxBatchNameLength + 1);

            await Assert.ThrowsAsync<ArgumentException>(
                () => Provider.GetRequiredService<IBatchManager>().Create(ctx => ctx.Enqueue(() => TestJobs.StaticWork()), tooLong));
        }

        [Fact]
        public async Task 批量任务执行_子任务工作名与批量项名一致()
        {
            // 加快轮询避免测试等待。批量进程每轮会把缓存时间推后一个周期，
            // 间隔过小会让它在同一轮里反复拉取同一个批量，这里给一个足够退出循环的值
            Option.SchedulePollingDelay = TimeSpan.FromMilliseconds(200);
            Option.WorkPollingDelay = TimeSpan.FromMilliseconds(100);

            var batchId = await CreateBatch("月结对账", 2);

            var process = new BatchWorkProcess(
                Lock,
                Query,
                Queue,
                Provider.GetRequiredService<IWorkStateHandlerProvider>(),
                CreateLogger<BatchWorkProcess>(),
                Option);

            // 第一轮：展开批量项，创建子工作
            await process.Execute(NewProcessContext());

            // 子任务工作由 BatchWorkProcess 创建，其名称应与批量项名一致
            var workNames = await Db.GetAllWorkNamesAsync();
            Assert.Equal(new[] { "月结对账#1", "月结对账#2" }, workNames.OrderBy(t => t));

            // 批量状态按子任务的<b>实际执行结果</b>收敛：子工作执行完之前不算成功。
            // 逐条 Perform 而不是 Execute —— 后者是订阅式长轮询，会在测试里阻塞
            var queueProcess = new QueueWorkProcess(CreateLogger<QueueWorkProcess>());
            foreach (var item in await Query.GetBatchItems(batchId, 0, 10))
            {
                Assert.False(string.IsNullOrEmpty(item.WorkId), $"{item.Name} 应已创建子工作");
                await queueProcess.Perform(NewProcessContext(),
                    new QueueInfo { WorkId = item.WorkId!, QueueName = Helper.DefaultQueue });
            }

            Assert.True(await WaitUntil(async () =>
            {
                await process.Execute(NewProcessContext());
                var current = await Query.GetBatchById(batchId);
                return current != null && current.Status == BatchStatus.Success;
            }, TimeSpan.FromSeconds(20)), "子任务全部执行成功后批量才算完成");
        }

        [Fact]
        public async Task 周期任务触发_任务名继承周期任务名()
        {
            var recurringManager = Provider.GetRequiredService<IRecurringWorkManager>();

            // "* * * * *"：每分钟命中一次，Trigger 会立即产生一次执行
            await recurringManager.AddOrUpdate("recurring-name-test",
                () => TestJobs.StaticWork(),
                "* * * * *",
                Helper.DefaultQueue,
                new RecurringWorkContext(),
                "月结对账（周期）");

            await recurringManager.Trigger("recurring-name-test");

            var definition = await Query.GetRecurringWorkById("recurring-name-test");
            Assert.NotNull(definition);
            Assert.False(string.IsNullOrEmpty(definition!.LastWorkId));

            Assert.Equal("月结对账（周期）", await WorkNameInDb(definition.LastWorkId));
        }

        [Fact]
        public async Task 周期任务_空名注册失败()
        {
            var recurringManager = Provider.GetRequiredService<IRecurringWorkManager>();

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => recurringManager.AddOrUpdate("recurring-empty-name",
                    () => TestJobs.StaticWork(),
                    "* * * * *",
                    Helper.DefaultQueue,
                    new RecurringWorkContext(),
                    string.Empty));
        }

        private async Task<string> CreateBatch(string name, int itemCount)
        {
            var batchManager = Provider.GetRequiredService<IBatchManager>();
            return await batchManager.Create(ctx =>
            {
                for (var i = 0; i < itemCount; i++)
                {
                    ctx.Enqueue(() => TestJobs.StaticWork());
                }
            }, name);
        }
    }
}
