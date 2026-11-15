using System;
using System.Threading.Tasks;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Works.States;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 验证过期数据清理（#20）：只删"已过期且终态"的工作/批量及其从表行，不误删仍在流转的任务。
    /// </summary>
    public class ExpiredDataCleanupTests : BackendWorkTestBase
    {
        private Task SeedWork(string id, string stateName, DateTime? expire)
            => Db.SeedWorkAsync(id, stateName, expire);

        private Task SeedState(string workId) => Db.SeedStateAsync(workId, SucceededWorkState.Name);

        private Task SeedQueue(string workId) => Db.SeedQueueAsync(workId, Helper.DefaultQueue);

        private Task<bool> WorkExists(string id) => Db.WorkExistsAsync(id);

        private Task<bool> QueueExists(string workId) => Db.QueueRowExistsAsync(workId);

        [Fact]
        public async Task RemoveExpiredWorks_只删过期终态_级联删状态与队列()
        {
            var now = Helper.CurrentUtcTime();
            // 过期终态：succeeded / deleted —— 应被清理
            await SeedWork("w_exp_succ", SucceededWorkState.Name, now.AddDays(-2));
            await SeedWork("w_exp_del", DeletedWorkState.Name, now.AddDays(-2));
            // 未过期终态：succeeded —— 保留
            await SeedWork("w_fresh_succ", SucceededWorkState.Name, now.AddDays(1));
            // 过期但非终态：enqueued —— 保留（仍在流转）
            await SeedWork("w_exp_enq", EnqueuedWorkState.Name, now.AddDays(-2));

            await SeedState("w_exp_succ");
            await SeedState("w_exp_del");
            await SeedQueue("w_exp_succ");
            await SeedQueue("w_exp_del");
            await SeedQueue("w_fresh_succ");
            await SeedQueue("w_exp_enq");

            await Storage.RemoveExpiredWorks(now);
            await Storage.Commit();

            // 过期终态被删，且级联删除其状态行与队列消息
            Assert.False(await WorkExists("w_exp_succ"));
            Assert.False(await WorkExists("w_exp_del"));
            Assert.False(await QueueExists("w_exp_succ"));
            Assert.False(await QueueExists("w_exp_del"));
            // 未过期 / 非终态保留，且其队列消息保留
            Assert.True(await WorkExists("w_fresh_succ"));
            Assert.True(await WorkExists("w_exp_enq"));
            Assert.True(await QueueExists("w_fresh_succ"));
            Assert.True(await QueueExists("w_exp_enq"));
        }

        private Task SeedBatch(string id, Batchs.BatchStatus status, DateTime expire)
            => Db.SeedBatchAsync(id, status, expire);

        private Task SeedBatchItem(string id, string batchId) => Db.SeedBatchItemAsync(id, batchId);

        private Task<bool> BatchExists(string id) => Db.BatchExistsAsync(id);

        private Task<bool> BatchItemExists(string id) => Db.BatchItemExistsAsync(id);

        [Fact]
        public async Task RemoveExpiredBatches_只删过期终态批量_级联删批量项()
        {
            var now = Helper.CurrentUtcTime();
            await SeedBatch("b_exp_succ", Batchs.BatchStatus.Success, now.AddDays(-2));
            await SeedBatch("b_exp_fail", Batchs.BatchStatus.Failure, now.AddDays(-2));
            await SeedBatch("b_fresh_succ", Batchs.BatchStatus.Success, now.AddDays(1));
            await SeedBatch("b_exp_init", Batchs.BatchStatus.Init, now.AddDays(-2)); // 非终态：保留

            await SeedBatchItem("bi_1", "b_exp_succ");
            await SeedBatchItem("bi_2", "b_exp_fail");

            await Storage.RemoveExpiredBatches(now);
            await Storage.Commit();

            Assert.False(await BatchExists("b_exp_succ"));
            Assert.False(await BatchExists("b_exp_fail"));
            Assert.False(await BatchItemExists("bi_1"));
            Assert.False(await BatchItemExists("bi_2"));
            // 未过期 / 非终态保留
            Assert.True(await BatchExists("b_fresh_succ"));
            Assert.True(await BatchExists("b_exp_init"));
        }

        [Fact]
        public async Task RemoveOrphanQueueRows_删除孤儿队列消息()
        {
            // 工作行不存在的队列消息（如 RemoveWork 未清理队列 / 崩溃残留）
            await SeedQueue("ghost-work");
            // 工作行存在，应保留
            await SeedWork("w_exists", SucceededWorkState.Name, DateTime.UtcNow.AddDays(1));
            await SeedQueue("w_exists");

            await Storage.RemoveOrphanQueueRows();
            await Storage.Commit();

            Assert.False(await QueueExists("ghost-work"));
            Assert.True(await QueueExists("w_exists"));
        }
    }
}
