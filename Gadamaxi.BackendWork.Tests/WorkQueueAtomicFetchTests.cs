using Gadamaxi.BackendWork.Sql;
using LinqToDB;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// #3 修复验证：出队去掉全局 WorkQueue 分布式锁，改为"条件 UPDATE + FetchedBy 回读"的原子抢占。
    /// 修复前：所有节点、所有消费线程共用一把全局锁，出队吞吐被锁死为单点串行；
    /// 修复后：并发安全由数据库行锁保证，同一个消费者只投递自己真正抢到的消息。
    /// </summary>
    public class WorkQueueAtomicFetchTests : BackendWorkTestBase
    {
        [Fact]
        public async Task Subscribe_并发订阅_每条消息只投递一次()
        {
            const int messageCount = 6;
            var workIds = new List<string>();
            for (var i = 0; i < messageCount; i++)
            {
                workIds.Add(await EnqueueSimpleWork());
            }

            var delivered = new ConcurrentBag<string>();
            var subscribers = Enumerable.Range(0, 4).Select(_ => Queue.Subscribe(
                new List<string> { Helper.DefaultQueue },
                ctx =>
                {
                    delivered.Add(ctx.Info.WorkId);
                    ctx.DealSuccess = true;
                    return Task.CompletedTask;
                })).ToArray();

            await Task.WhenAll(subscribers);

            // 并发订阅下每条消息恰好被投递一次：既不能漏投，也不能重复投递
            Assert.Equal(messageCount, delivered.Count);
            Assert.Equal(messageCount, delivered.Distinct().Count());
        }

        [Fact]
        public async Task Subscribe_抢占成功后写入本次抢占标记()
        {
            var workId = await EnqueueSimpleWork();

            var delivered = 0;
            await Queue.Subscribe(new List<string> { Helper.DefaultQueue }, ctx =>
            {
                Interlocked.Increment(ref delivered);
                // 不确认：模拟任务仍在执行，保留消息以便检查抢占标记
                return Task.CompletedTask;
            });

            Assert.Equal(1, delivered);
            Assert.False(string.IsNullOrEmpty(await FetchedByOf(workId)), "抢占成功后应写入 FetchedBy 标记");
        }

        [Fact]
        public async Task Subscribe_不受全局WorkQueue锁阻塞()
        {
            var workId = await EnqueueSimpleWork();

            // 占住"WorkQueue"这把全局锁：修复前出队的第一步就是获取它，
            // 锁被占用时整个出队会被阻塞（吞吐被单点串行化的根因）。
            using (await Lock.AcquireLock(nameof(WorkQueue), TimeSpan.FromMinutes(1)))
            {
                var delivered = 0;
                await Queue.Subscribe(new List<string> { Helper.DefaultQueue }, ctx =>
                {
                    Interlocked.Increment(ref delivered);
                    ctx.DealSuccess = true;
                    return Task.CompletedTask;
                });

                Assert.Equal(1, delivered);
            }
        }

        private Task<string?> FetchedByOf(string workId) => Db.GetQueueFetchedByAsync(workId);
    }
}
