using Gadamaxi.BackendWork.Mongodb.Documents;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Mongodb
{
    /// <summary>
    /// Mongodb 版队列。出队沿用 Sql 版的"原子抢占 + 可见性超时"语义（无全局锁）：
    /// <list type="number">
    /// <item>无锁地选出候选行；</item>
    /// <item>条件 UPDATE 抢占：filter 里再次校验可见性，Mongodb 的<b>单文档更新是原子的</b>，
    ///       同一时刻只有一个抢占者能改写成功，并写入本次抢占的唯一标记 <c>FetchedBy</c>；</item>
    /// <item>按 FetchedBy 回读，只有真正抢到的行才会被投递；</item>
    /// <item>处理成功（DealSuccess）后才删除；失败或进程崩溃时保留，
    ///       超过可见性超时（<see cref="Helper.QueueFetchTimeoutMinutes"/>）后可被重新投递。</item>
    /// </list>
    /// </summary>
    public class WorkQueue : IWorkQueue
    {
        private readonly MongoBackendWorkContext _context;
        private readonly WorkServerOption _serverOption;
        private readonly ILogger<WorkQueue>? _logger;

        /// <param name="logger">
        /// 可选日志器。刻意不走服务定位器：没有配置日志时也必须能出队，
        /// 取不到就是 null，而不是抛异常（Sql 版在宿主未装配 DI 时会因此起不来）。
        /// </param>
        public WorkQueue(MongoBackendWorkContext context, WorkServerOption? serverOption = null, ILogger<WorkQueue>? logger = null)
        {
            Helper.CheckArgumentNull(context, nameof(context));

            _context = context;
            _serverOption = serverOption ?? new WorkServerOption();
            _logger = logger;
        }

        public async Task Publish(QueueInfo info)
        {
            var doc = new QueueDocument
            {
                WorkId = info.WorkId,
                Queue = info.QueueName,
                FetchUtcTime = null,
                FetchedBy = null
            };

            await _context.Queues.InsertOneAsync(doc);
        }

        public async Task Subscribe(List<string> queues, Func<QueueSubscribeContext, Task> subscribeAction)
        {
            var now = Helper.CurrentUtcTime();
            var fetchDeadline = now.AddMinutes(-Helper.QueueFetchTimeoutMinutes);

            // 本次抢占的唯一标记：既是"谁取走的"，也用于回读时判定本次是否抢到。
            // 前缀取节点名（与 Server.ServerName 一致，面板两侧才能对上）。
            var nodeName = string.IsNullOrEmpty(_serverOption.ServerName)
                ? Environment.MachineName
                : _serverOption.ServerName;
            var fetchToken = $"{nodeName}#{Helper.NewId()}";

            var visible = Builders<QueueDocument>.Filter.Or(
                Builders<QueueDocument>.Filter.Eq(t => t.FetchUtcTime, null),
                Builders<QueueDocument>.Filter.Lt(t => t.FetchUtcTime, fetchDeadline));

            // 1) 候选查询：不加锁，允许各节点/各线程并发读取。
            //    ObjectId 本身按生成时刻递增，排序即得先进先出。
            var candidates = await _context.Queues
                .Find(Builders<QueueDocument>.Filter.And(Builders<QueueDocument>.Filter.In(t => t.Queue, queues), visible))
                .SortBy(t => t.Id)
                .Limit(_serverOption.GetBatchSize())
                .Project(t => t.Id)
                .ToListAsync();

            if (candidates.Count == Helper.Zero)
            {
                return;
            }

            // 2) 条件更新抢占：被别人抢先改写过的行 FetchUtcTime 已刷新到"现在"，
            //    不再满足可见性条件，因此更新 0 匹配 —— 抢占的原子性由单文档更新保证。
            var update = Builders<QueueDocument>.Update
                .Set(t => t.FetchUtcTime, now)
                .Set(t => t.FetchedBy, fetchToken);

            foreach (var id in candidates)
            {
                var filter = Builders<QueueDocument>.Filter.And(
                    Builders<QueueDocument>.Filter.Eq(t => t.Id, id),
                    visible);

                await _context.Queues.UpdateOneAsync(filter, update);
            }

            // 3) 只投递本次真正抢到的行
            var fetched = await _context.Queues
                .Find(Builders<QueueDocument>.Filter.And(
                    Builders<QueueDocument>.Filter.In(t => t.Id, candidates),
                    Builders<QueueDocument>.Filter.Eq(t => t.FetchedBy, fetchToken)))
                .SortBy(t => t.Id)
                .ToListAsync();

            foreach (var item in fetched)
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
                    _logger?.LogWarning($"Process queue message {ctx.Info} failure.{ex.Message}\n{ex.GetStacks()}");
                    continue;
                }

                if (ctx.DealSuccess)
                {
                    // 任务已确认处理（完成或已进入状态机重试路径），消息出队
                    await _context.Queues.DeleteOneAsync(Builders<QueueDocument>.Filter.Eq(t => t.Id, item.Id));
                }
            }
        }
    }
}
