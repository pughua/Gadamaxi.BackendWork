using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Sql;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// P0-1 修复验证：队列消息“抢占标记 + 可见性超时”，消息不丢失。
    /// 修复前：出队即删行，处理节点崩溃后消息永久丢失。
    /// </summary>
    public class WorkQueueFetchTests : BackendWorkTestBase
    {
        [Fact]
        public async Task Subscribe_处理异常时_消息保留_不删除()
        {
            var workId = await EnqueueSimpleWork();
            Assert.True(await QueueRowExists(workId), "入队后应存在队列消息");

            // 模拟处理节点在处理消息时崩溃（回调抛出异常）
            await Queue.Subscribe(new List<string> { Helper.DefaultQueue }, ctx =>
            {
                throw new InvalidOperationException("simulate worker crash");
            });

            // 修复点：消息被标记取走但保留，等待可见性超时后重新投递
            Assert.True(await QueueRowExists(workId), "处理异常后消息不应被删除");
            Assert.True(await WorkRowExists(workId), "工作数据应保留");
        }

        [Fact]
        public async Task Subscribe_可见性超时内_消息不会被重复投递()
        {
            var workId = await EnqueueSimpleWork();

            var delivered = 0;
            await Queue.Subscribe(new List<string> { Helper.DefaultQueue }, ctx =>
            {
                // 取走后不确认（DealSuccess = false），模拟任务还在执行
                return Task.CompletedTask;
            });

            // 立刻再次订阅：消息刚被取走，尚在可见性超时内，不应重复投递
            await Queue.Subscribe(new List<string> { Helper.DefaultQueue }, ctx =>
            {
                Interlocked.Increment(ref delivered);
                ctx.DealSuccess = true;
                return Task.CompletedTask;
            });

            Assert.Equal(0, delivered);
            Assert.True(await QueueRowExists(workId), "未确认完成前消息应保留");
        }

        [Fact]
        public async Task Subscribe_消息崩溃后_可见性超时到期_重新投递并确认删除()
        {
            var workId = await EnqueueSimpleWork();

            // 第一轮：取走消息后处理节点“崩溃”（不确认）
            await Queue.Subscribe(new List<string> { Helper.DefaultQueue }, ctx => Task.CompletedTask);
            Assert.True(await QueueRowExists(workId));

            // 模拟时间流逝超过可见性超时（QueueFetchTimeoutMinutes = 5 分钟）
            await AgeQueueRow(workId, TimeSpan.FromMinutes(Helper.QueueFetchTimeoutMinutes + 1));

            // 第二轮：消息被重新投递，处理后确认删除
            var delivered = 0;
            await Queue.Subscribe(new List<string> { Helper.DefaultQueue }, ctx =>
            {
                Interlocked.Increment(ref delivered);
                ctx.DealSuccess = true;
                return Task.CompletedTask;
            });

            Assert.Equal(1, delivered);
            Assert.False(await QueueRowExists(workId), "确认完成后消息应出队");
        }
    }
}
