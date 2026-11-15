using System;
using System.Linq;
using System.Threading.Tasks;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 批量失败重试：IBatchManager.Retry 的落库契约 + 真实调度下的端到端行为。
    /// 核心语义是"只重新执行未成功的子任务，已成功的跳过"。
    /// </summary>
    public class BatchRetryTests : BackendWorkTestBase
    {
        private IBatchManager BatchManager => Provider.GetRequiredService<IBatchManager>();

        /// <param name="deleteDatabaseOnDispose">
        /// 端到端用例启动的是真实后台线程（无法被真正停止），保留库文件避免残留线程重建已删除的库
        /// </param>
        public BatchRetryTests() : base(deleteDatabaseOnDispose: false)
        {
            // 关闭失败自动重试：让子任务首次失败即进入终态，测试无需等待退避重试
            Option.WorkRetryCount = 0;
        }

        [Fact]
        public async Task Retry_只重置未成功的批量项_已成功的保持不动()
        {
            var batchId = await BatchManager.Create(ctx =>
            {
                ctx.Enqueue(() => TestJobs.StaticWork());
                ctx.Enqueue(() => TestJobs.StaticWork());
                ctx.Enqueue(() => TestJobs.StaticWork());
            }, "重试契约");

            var items = await Query.GetBatchItems(batchId, 0, 10);
            var first = items.Single(t => t.Name == "重试契约#1");
            var second = items.Single(t => t.Name == "重试契约#2");

            // #1 已成功（子工作已完成），#2 已失败（子工作终态失败），#3 尚未展开
            var successWorkId = await CreateSucceededWork("已成功子任务");
            var failedWorkId = await CreateFailedWork("已失败子任务");
            await SetItem(batchId, first.Id, BatchStatus.Success, successWorkId);
            await SetItem(batchId, second.Id, BatchStatus.Failure, failedWorkId);

            await BatchManager.Retry(batchId);

            var after = await Query.GetBatchItems(batchId, 0, 10);
            var firstAfter = after.Single(t => t.Name == "重试契约#1");
            var secondAfter = after.Single(t => t.Name == "重试契约#2");
            var thirdAfter = after.Single(t => t.Name == "重试契约#3");

            // 已成功的子任务：状态与子工作 Id 都原样保留，不会被重跑
            Assert.Equal(BatchStatus.Success, firstAfter.Status);
            Assert.Equal(successWorkId, firstAfter.WorkId);

            // 失败与未展开的批量项：重置为待展开，并解绑旧的子工作（重新展开时会创建新子工作）
            Assert.Equal(BatchStatus.Init, secondAfter.Status);
            Assert.True(string.IsNullOrEmpty(secondAfter.WorkId), "失败批量项应解绑旧的子工作 Id");
            Assert.Equal(BatchStatus.Init, thirdAfter.Status);

            var batch = await Query.GetBatchById(batchId);
            Assert.NotNull(batch);
            Assert.Equal(BatchStatus.Init, batch!.Status);
            Assert.Equal(1, batch.RetryCount);
            // 已展开的计数回退到"保留下来的批量项数"，重新展开时再累加
            Assert.Equal(1, batch.DealCount);
            // 重新登记进批量调度缓存，BatchWorkProcess 才会再次拉取
            Assert.True(await Query.CheckCacheSet(Helper.BatchKey, batchId));
        }

        [Fact]
        public async Task Retry_执行中的子任务被跳过()
        {
            var batchId = await BatchManager.Create(ctx =>
            {
                ctx.Enqueue(() => TestJobs.StaticWork());
                ctx.Enqueue(() => TestJobs.StaticWork());
            }, "重试跳过执行中");

            var items = await Query.GetBatchItems(batchId, 0, 10);
            var running = items.Single(t => t.Name == "重试跳过执行中#1");

            // 子工作仍在队列中等待执行：重试不能重复投递它
            var runningWorkId = await Manager.Enqueue(() => TestJobs.StaticWork(), "执行中子任务");
            await SetItem(batchId, running.Id, BatchStatus.Processing, runningWorkId);

            await BatchManager.Retry(batchId);

            var after = await Query.GetBatchItems(batchId, 0, 10);
            var runningAfter = after.Single(t => t.Name == "重试跳过执行中#1");
            var idleAfter = after.Single(t => t.Name == "重试跳过执行中#2");

            Assert.Equal(BatchStatus.Processing, runningAfter.Status);
            Assert.Equal(runningWorkId, runningAfter.WorkId);
            Assert.Equal(BatchStatus.Init, idleAfter.Status);

            var batch = await Query.GetBatchById(batchId);
            Assert.Equal(1, batch!.DealCount);
        }

        [Fact]
        public async Task Retry_批量不存在_抛异常()
        {
            await Assert.ThrowsAsync<BackendException>(() => BatchManager.Retry("not-exist-batch"));
        }

        [Fact]
        public async Task Retry_批量已成功_抛异常()
        {
            var batchId = await BatchManager.Create(ctx => ctx.Enqueue(() => TestJobs.StaticWork()), "已完成批量");

            using (var storage = Provider.GetRequiredService<IWorkStorage>())
            {
                var batch = await Query.GetBatchById(batchId);
                batch!.Status = BatchStatus.Success;
                await storage.UpdateBatch(batch);
                await storage.Commit();
            }

            await Assert.ThrowsAsync<BackendException>(() => BatchManager.Retry(batchId));
        }

        [Fact]
        public async Task Retry_等待父批量的批量_抛异常()
        {
            var parentId = await BatchManager.Create(ctx => ctx.Enqueue(() => TestJobs.StaticWork()), "父批量");
            var childId = await BatchManager.CreateContinue(parentId,
                ctx => ctx.Enqueue(() => TestJobs.StaticWork()), "子批量");

            await Assert.ThrowsAsync<BackendException>(() => BatchManager.Retry(childId));
        }

        [Fact]
        public async Task Retry_空批量_只重置状态()
        {
            var batchId = await BatchManager.Create(ctx => { }, "空批量重试");

            await BatchManager.Retry(batchId);

            var batch = await Query.GetBatchById(batchId);
            Assert.Equal(BatchStatus.Init, batch!.Status);
            Assert.Equal(0, batch.DealCount);
            Assert.Equal(1, batch.RetryCount);
        }

        [Fact]
        public async Task Retry_批量标识为空_抛异常()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => BatchManager.Retry(string.Empty));
        }

        /// <summary>
        /// 端到端：真实调度跑批量，其中一个子任务失败 → 批量失败 → 用户重试后
        /// 只有失败的子任务被重新执行，已成功的子任务不再执行。
        /// </summary>
        [Fact]
        public async Task 全链路_批量失败后重试_只重跑失败的子任务()
        {
            Option.ServerName = "batch-retry-server";
            Option.WorkPollingDelay = TimeSpan.FromMilliseconds(500);
            Option.SchedulePollingDelay = TimeSpan.FromMilliseconds(500);
            Option.RecurringPollingDelay = TimeSpan.FromMinutes(10);
            Option.RecoveryPollingDelay = TimeSpan.FromMinutes(10);
            Option.WorkProcessingTimeout = TimeSpan.FromHours(10);
            TestJobs.Reset();

            var batchId = await BatchManager.Create(ctx =>
            {
                ctx.Enqueue(() => TestJobs.Mark("retry-1"));
                // 首次执行失败、重试后成功的子任务
                ctx.Enqueue(() => TestJobs.Flaky("retry-2", 1));
                ctx.Enqueue(() => TestJobs.Mark("retry-3"));
            }, "重试E2E");

            var server = Provider.GetRequiredService<IWorkServer>();
            var runTask = server.Run();
            try
            {
                Assert.True(await WaitUntil(async () =>
                {
                    var batch = await Query.GetBatchById(batchId);
                    return batch != null && batch.Status == BatchStatus.Failure;
                }, TimeSpan.FromSeconds(60)), await DumpBatch(batchId));

                // 批量一有失败项就会标记失败，此时其余子任务可能还在跑：
                // 等它们执行完再断言，否则"各执行过一次"会偶发落空
                Assert.True(await WaitUntil(() => Task.FromResult(Count("retry-1") == 1 && Count("retry-3") == 1),
                    TimeSpan.FromSeconds(30)), await DumpBatch(batchId));

                // 批量失败时：成功的子任务各执行一次，失败的子任务也执行了一次（失败）
                Assert.Equal(1, Count("retry-1"));
                Assert.Equal(1, Count("retry-3"));
                Assert.Equal(1, Count("retry-2"));

                var failedItem = (await Query.GetBatchItems(batchId, 0, 10))
                    .Single(t => t.Name == "重试E2E#2");
                Assert.Equal(BatchStatus.Failure, failedItem.Status);
                // 失败原因必须落在批量项上，且是业务根因：
                // 不能是空串（旧实现取 NewState.Info，执行链路传空串），
                // 也不能是反射调用的包装异常文本（旧实现未解包 TargetInvocationException）
                Assert.Contains("flaky failure", failedItem.Message);

                await BatchManager.Retry(batchId);

                Assert.True(await WaitUntil(async () =>
                {
                    var batch = await Query.GetBatchById(batchId);
                    return batch != null && batch.Status == BatchStatus.Success;
                }, TimeSpan.FromSeconds(60)), await DumpBatch(batchId));
            }
            finally
            {
                await server.Stop();
            }

            if (runTask.IsFaulted)
            {
                throw runTask.Exception!.InnerExceptions[0];
            }

            // 已成功的子任务没有被重跑，只有失败的子任务被重新执行了一次
            Assert.Equal(1, Count("retry-1"));
            Assert.Equal(1, Count("retry-3"));
            Assert.Equal(2, Count("retry-2"));

            var items = await Query.GetBatchItems(batchId, 0, 10);
            Assert.All(items, t => Assert.Equal(BatchStatus.Success, t.Status));
            Assert.All(items, t => Assert.False(string.IsNullOrEmpty(t.WorkId)));

            // 重试后重新展开的子工作：批量项绑定到新的子工作且已成功
            var retried = items.Single(t => t.Name == "重试E2E#2");
            Assert.Equal(SucceededWorkState.Name, (await Query.GetLastState(retried.WorkId!))!.StateName);
            Assert.Equal("重试E2E#2", await WorkNameInDb(retried.WorkId!));
        }

        #region helpers

        private static int Count(string marker) => TestJobs.Records.Count(t => t == marker);

        private async Task<string> DumpBatch(string batchId)
        {
            var batch = await Query.GetBatchById(batchId);
            var items = await Query.GetBatchItems(batchId, 0, 10);
            var details = new System.Collections.Generic.List<string>();
            foreach (var item in items)
            {
                var state = string.IsNullOrEmpty(item.WorkId)
                    ? "no-work"
                    : (await Query.GetLastState(item.WorkId))?.StateName ?? "no-state";
                details.Add($"{item.Name}({item.Status},{state},work={item.WorkId})");
            }

            return $"批量状态={batch?.Status}，批量项=[{string.Join(", ", details)}]";
        }

        private async Task SetItem(string batchId, string itemId, BatchStatus status, string workId)
        {
            using (var storage = Provider.GetRequiredService<IWorkStorage>())
            {
                await storage.UpdateBatchItem(batchId, itemId, status, string.Empty, workId);
                await storage.Commit();
            }
        }

        /// <summary>创建一个已成功完成的工作（用于构造"已成功的批量项"）</summary>
        private async Task<string> CreateSucceededWork(string name)
        {
            var workId = await Manager.Enqueue(() => TestJobs.StaticWork(), name);
            var data = await Query.GetWorkData(workId);
            await WorkFactory.ChangeState(workId,
                new SucceededWorkState(null, 1, "test-server"),
                data!.StateName, data);
            return workId;
        }

        /// <summary>创建一个终态失败的工作（用于构造"已失败的批量项"）</summary>
        private async Task<string> CreateFailedWork(string name)
        {
            var workId = await Manager.Enqueue(() => TestJobs.StaticWork(), name);
            var data = await Query.GetWorkData(workId);
            await WorkFactory.ChangeState(workId,
                new FailedWorkState(new InvalidOperationException("boom"), 1, "test-server", "test"),
                data!.StateName, data);
            return workId;
        }

        #endregion
    }
}
