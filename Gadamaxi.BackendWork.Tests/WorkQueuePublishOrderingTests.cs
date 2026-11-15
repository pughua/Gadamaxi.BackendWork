using System.Collections.Generic;
using System.Threading.Tasks;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 入队消息与工作行的可见顺序验证。
    /// 存储写入是延迟事务（Commit 才落库），若队列消息用独立连接先落库，
    /// 消费端会在工作行可见之前取到消息，读到"工作不存在"就把消息确认掉，任务被永久丢弃。
    /// 这类缺陷只在并发下偶发（表现是任务卡在 enqueued / awaiting 且队列为空），
    /// 这里用"提交前/提交后"的确定性断言把它钉住。
    /// </summary>
    public class WorkQueuePublishOrderingTests : BackendWorkTestBase
    {
        [Fact]
        public async Task 入队消息与工作行_在同一事务中可见()
        {
            using var storage = DependencyInjectionHelper.Get<IWorkStorage>();
            var workload = Workload.FromExpression(() => TestJobs.StaticWork());

            var workId = await WorkFactory.CreateEnqueueWork(storage,
                Queue,
                Provider.GetRequiredService<IWorkStateHandlerProvider>(),
                workload,
                Helper.CurrentUtcTime(),
                "test",
                new Dictionary<string, object>(),
                "入队顺序验证");

            Assert.False(await QueueRowExists(workId), "提交前队列不应有消息");
            Assert.False(await WorkRowExists(workId), "提交前工作行不应可见");

            await storage.Commit();

            Assert.True(await WorkRowExists(workId));
            Assert.True(await QueueRowExists(workId), "提交后消息才对工作行可见");
            Assert.Equal(Helper.DefaultQueue, await QueueNameOf(workId));
        }

        [Fact]
        public async Task 连续任务_父任务完成后入队_消息与工作行同时可见()
        {
            var parentId = await Manager.Enqueue(() => TestJobs.StaticWork(), "父任务");
            var childId = await Manager.Awaiting(() => TestJobs.StaticWork(), parentId, "连续任务");

            // 创建路径已提交，子任务仍在 awaiting
            Assert.True(await WorkRowExists(childId));
            Assert.False(await QueueRowExists(childId));

            // 父任务结束唤醒子任务：唤醒用的是独立事务，提交后消息与状态一起可见
            await WorkFactory.EnqueueAwaitingChildren(Query, parentId);

            Assert.True(await QueueRowExists(childId));
            Assert.Equal(Helper.DefaultQueue, await QueueNameOf(childId));
        }
    }
}
