using System;
using System.Linq;
using System.Threading.Tasks;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// IBatchManager.Create / CreateContinue 与 BatchContext 各加项入口的落库契约验证。
    /// 批量创建只登记批量与批量项（子工作由 BatchWorkProcess 后续创建），
    /// 因此这里断言的是"登记结果"：批量状态、计数、父子关系、批量项状态与名称。
    /// </summary>
    public class BatchManagerApiTests : BackendWorkTestBase
    {
        private IBatchManager BatchManager => Provider.GetRequiredService<IBatchManager>();

        [Fact]
        public async Task Create_批量与批量项落库_无父批次时为待展开状态()
        {
            var batchId = await BatchManager.Create(ctx =>
            {
                ctx.Enqueue(() => TestJobs.StaticWork());
                ctx.Enqueue<InstanceJobs>(j => j.Write("batch-item"));
                ctx.Schedule(() => TestJobs.StaticWorkAsync(), TimeSpan.FromMinutes(1));
            }, "批量API测试");

            Assert.False(string.IsNullOrEmpty(batchId));

            var batch = await Query.GetBatchById(batchId);
            Assert.NotNull(batch);
            Assert.Equal("批量API测试", batch!.Name);
            Assert.Equal(3, batch.Count);
            Assert.Equal(0, batch.DealCount);
            // Awaiting 表示"等父批次完成"；无父批次时由状态管道直接落到 Init（可立即展开）
            Assert.Equal(BatchStatus.Init, batch.Status);
            Assert.Equal(string.Empty, batch.ParentBatchId);
            Assert.Equal("批量API测试", await BatchNameInDb(batchId));

            var items = await Query.GetBatchItems(batchId, 0, 10);
            Assert.Equal(3, items.Count);
            Assert.All(items, t => Assert.Equal(BatchStatus.Init, t.Status));
            Assert.All(items, t => Assert.True(string.IsNullOrEmpty(t.WorkId)));

            // 子任务名按加入顺序派生：批量名 + 序号
            Assert.Equal(new[] { "批量API测试#1", "批量API测试#2", "批量API测试#3" },
                items.Select(t => t.Name).OrderBy(t => t).ToArray());

            // 登记到批量调度缓存，BatchWorkProcess 据此拉取
            Assert.True(await Query.CheckCacheSet(Helper.BatchKey, batchId));
        }

        [Fact]
        public async Task CreateContinue_记录父批次()
        {
            var parentId = await BatchManager.Create(ctx => ctx.Enqueue(() => TestJobs.StaticWork()), "父批量");

            var childId = await BatchManager.CreateContinue(parentId,
                ctx => ctx.Enqueue(() => TestJobs.StaticWork()), "子批量");

            var child = await Query.GetBatchById(childId);
            Assert.NotNull(child);
            Assert.Equal(parentId, child!.ParentBatchId);
            Assert.Equal("子批量", child.Name);
            Assert.Equal(1, child.Count);
            // 父批次存在：状态为 Awaiting，登记在父批次名下，等父批次完成后才展开
            Assert.Equal(BatchStatus.Awaiting, child.Status);
            Assert.True(await Query.CheckCacheSet($"{Helper.BatchKey}:{parentId}", childId));
        }

        [Fact]
        public async Task Create_空批量_计数为零()
        {
            var batchId = await BatchManager.Create(ctx => { }, "空批量");

            var batch = await Query.GetBatchById(batchId);
            Assert.NotNull(batch);
            Assert.Equal(0, batch!.Count);

            Assert.Empty(await Query.GetBatchItems(batchId, 0, 10));
        }

        [Fact]
        public async Task 批量项_Enqueue入口_登记为入队状态()
        {
            var batchId = await BatchManager.Create(ctx => ctx.Enqueue(() => TestJobs.StaticWork()), "入队批量项");

            var item = (await Query.GetBatchItems(batchId, 0, 10)).Single();
            Assert.Equal(EnqueuedWorkState.Name, ItemStateName(item));
        }

        [Fact]
        public async Task 批量项_Schedule入口_登记为定时状态()
        {
            var batchId = await BatchManager.Create(
                ctx => ctx.Schedule(() => TestJobs.StaticWorkAsync(), TimeSpan.FromMinutes(1)), "定时批量项");

            var item = (await Query.GetBatchItems(batchId, 0, 10)).Single();
            Assert.Equal(ScheduledWorkState.Name, ItemStateName(item));
        }

        [Fact]
        public async Task 批量项_Awaiting入口_登记为连续状态并记录父项()
        {
            var batchId = await BatchManager.Create(ctx =>
            {
                var first = ctx.Enqueue(() => TestJobs.StaticWork());
                ctx.Awaiting(() => TestJobs.StaticWork(), first);
            }, "连续批量项");

            var items = await Query.GetBatchItems(batchId, 0, 10);
            Assert.Equal(2, items.Count);

            var awaiting = items.Single(t => t.Name == "连续批量项#2");
            var workData = Assert.IsType<BatchItemWorkData>(awaiting.Data);
            Assert.Equal(AwaitingWorkState.Name, workData.State.StateName);
            Assert.True(workData.State.Data.ContainsKey(nameof(AwaitingWorkState.ParentId)));
        }

        [Fact]
        public async Task 批量项_Workload重载入口_同样派生名称()
        {
            var batchId = await BatchManager.Create(ctx =>
            {
                ctx.Schedule(Workload.FromExpression(() => TestJobs.StaticWork()), TimeSpan.FromMinutes(1));
                ctx.Awaiting(Workload.FromExpression(() => TestJobs.StaticWork()), "not-exist-parent");
            }, "负载批量项");

            var names = (await Query.GetBatchItems(batchId, 0, 10)).Select(t => t.Name).OrderBy(t => t).ToArray();
            Assert.Equal(new[] { "负载批量项#1", "负载批量项#2" }, names);
        }

        [Fact]
        public async Task Create_空名_抛异常()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => BatchManager.Create(ctx => ctx.Enqueue(() => TestJobs.StaticWork()), string.Empty));
        }

        [Fact]
        public async Task Create_批量名超过预留长度_抛异常()
        {
            var tooLong = new string('x', Helper.MaxBatchNameLength + 1);

            await Assert.ThrowsAsync<ArgumentException>(
                () => BatchManager.Create(ctx => ctx.Enqueue(() => TestJobs.StaticWork()), tooLong));
        }

        [Fact]
        public async Task Create_回调为空_抛异常()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => BatchManager.Create(null!, "空回调"));
        }

        [Fact]
        public async Task CreateContinue_父批次为空_抛异常()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => BatchManager.CreateContinue(string.Empty, ctx => ctx.Enqueue(() => TestJobs.StaticWork()), "子批量"));
        }

        private static string ItemStateName(BatchItem item)
        {
            var workData = Assert.IsType<BatchItemWorkData>(item.Data);
            return workData.State.StateName;
        }
    }
}
