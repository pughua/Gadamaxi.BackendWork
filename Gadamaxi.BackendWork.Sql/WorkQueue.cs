using Gadamaxi.BackendWork.Storages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using LinqToDB;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Sql.Tables;
using LinqToDB.Async;

namespace Gadamaxi.BackendWork.Sql
{
    public class WorkQueue : IWorkQueue
    {
        private readonly DataOptions _options;
        private readonly ILogger<WorkQueue> _logger;
        private readonly WorkServerOption _serverOption;
        public WorkQueue(DataOptions options, WorkServerOption serverOption)
        {
            _options = options;
            // 出队已改为无全局锁的原子抢占（#3）：并发安全由"条件 UPDATE + FetchedBy 回读"保证，
            // 不再需要一把把所有节点串行化的 WorkQueue 全局分布式锁
            _logger = DependencyInjectionHelper.Get<ILogger<WorkQueue>>();
            _serverOption = serverOption;
        }

        public async Task Publish(QueueInfo info)
        {
            using (var db = new DBConnection(_options))
            {
                await db.QueueTable
                    .Value(t => t.WorkId, info.WorkId)
                    .Value(t => t.Queue, info.QueueName)
                    .InsertAsync();
            }
        }

        /// <summary>
        /// 出队采用“原子抢占 + 可见性超时”语义（消息不丢失、且不加全局锁）：
        /// 1. 无锁地选出候选行；
        /// 2. 条件 UPDATE 抢占：WHERE 里再次校验可见性，由数据库行锁保证同一行只有一个抢占者能改写，
        ///    同时写入本次抢占的唯一标记 FetchedBy；
        /// 3. 按 FetchedBy 回读，只有真正抢到的行才会被投递（并发下后写者覆盖标记，先写者读不到）；
        /// 4. 消息处理成功（DealSuccess = true）后才删除；失败或进程崩溃时保留，
        ///    超过可见性超时（Helper.QueueFetchTimeoutMinutes）后可被重新投递。
        /// <para>
        /// 修复前所有节点、所有消费线程共用一把 WorkQueue 全局分布式锁，出队吞吐被锁死为单点串行（#3）。
        /// </para>
        /// </summary>
        public async Task Subscribe(List<string> queues, Func<QueueSubscribeContext, Task> subscribeAction)
        {
            var now = Helper.CurrentUtcTime();
            var fetchDeadline = now.AddMinutes(-Helper.QueueFetchTimeoutMinutes);
            // 本次抢占的唯一标记：既是"谁取走的"，也用于回读时判定本次是否抢到
            // 前缀取节点名（与 Server.ServerName 一致，面板两侧才能对上），
            // 后缀仍是一次性唯一标记，用于并发抢占回读判定（不可去掉，否则无法区分本次抢占）
            var nodeName = string.IsNullOrEmpty(_serverOption.ServerName)
                ? Environment.MachineName
                : _serverOption.ServerName;
            var fetchToken = $"{nodeName}#{Helper.NewId()}";
            var data = new List<QueueTable>();

            using (var db = new DBConnection(_options))
            {
                // 1) 候选查询：不加锁，允许各节点/各线程并发读取
                var candidates = await (from s in db.QueueTable
                                        where queues.Contains(s.Queue)
                                              && (s.FetchUtcTime == null || s.FetchUtcTime < fetchDeadline)
                                        orderby s.Id
                                        select s.Id)
                                       // 每轮扫描上限取自配置（#22）：积压时可调大，不再固定为常量 10
                                       .Take(_serverOption.GetBatchSize())
                                       .ToListAsync();

                if (candidates.Count > Helper.Zero)
                {
                    // 2) 条件更新抢占：被别人抢先改写过的行 FetchUtcTime 已刷新到"现在"，
                    //    不再满足可见性条件，因此更新 0 行 —— 抢占的原子性由行锁保证
                    await db.QueueTable
                        .Where(t => candidates.Contains(t.Id)
                                    && (t.FetchUtcTime == null || t.FetchUtcTime < fetchDeadline))
                        .Set(t => t.FetchUtcTime, now)
                        .Set(t => t.FetchedBy, fetchToken)
                        .UpdateAsync();

                    // 3) 只投递本次真正抢到的行
                    data = await (from s in db.QueueTable
                                  where candidates.Contains(s.Id) && s.FetchedBy == fetchToken
                                  select s).ToListAsync();
                }
            }

            foreach (var item in data)
            {
                var ctx = new QueueSubscribeContext(new QueueInfo
                {
                    WorkId = item.WorkId,
                    QueueName = item.Queue
                })
                {
                    DealSuccess = false
                };

                try
                {
                    await subscribeAction(ctx);
                }
                catch (Exception ex)
                {
                    // 单条消息处理异常不影响其它消息；消息保留，等可见性超时后重新投递
                    _logger.LogWarning($"Process queue message {ctx.Info} failure.{ex.Message}\n{ex.GetStacks()}");
                    continue;
                }

                if (ctx.DealSuccess)
                {
                    // 任务已确认处理（完成或已进入状态机重试路径），消息出队
                    using (var db = new DBConnection(_options))
                    {
                        await db.QueueTable.Where(t => t.Id == item.Id).DeleteAsync();
                    }
                }
            }
        }
    }
}
