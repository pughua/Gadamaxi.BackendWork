using Gadamaxi.BackendWork.Mongodb.Documents;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Mongodb
{
    /// <summary>
    /// Mongodb 存储上下文：持有客户端 / 库 / 全部集合，并负责索引的幂等创建与事务能力探测。
    /// <para>
    /// 集合与 Sql 的表一一对应（Work / State / Queue / CacheSet / Recurring / Server / Lock /
    /// Batch / BatchItem / Saga / SagaStep），列结构也保持一致，因此两套存储可以互换而不改上层。
    /// </para>
    /// </summary>
    public class MongoBackendWorkContext
    {
        private readonly ILogger<MongoBackendWorkContext>? _logger;
        private int _indexesEnsured;

        public MongoBackendWorkContext(MongoBackendWorkOptions options, ILoggerFactory? loggerFactory = null)
        {
            Helper.CheckArgumentNull(options, nameof(options));

            Options = options;
            _logger = loggerFactory?.CreateLogger<MongoBackendWorkContext>();

            Client = new MongoClient(options.BuildSettings());
            Database = Client.GetDatabase(options.DatabaseName);

            Works = Database.GetCollection<WorkDocument>(CollectionName<WorkDocument>());
            States = Database.GetCollection<StateDocument>(CollectionName<StateDocument>());
            Queues = Database.GetCollection<QueueDocument>(CollectionName<QueueDocument>());
            CacheSets = Database.GetCollection<CacheSetDocument>(CollectionName<CacheSetDocument>());
            Recurrings = Database.GetCollection<RecurringDocument>(CollectionName<RecurringDocument>());
            Servers = Database.GetCollection<ServerDocument>(CollectionName<ServerDocument>());
            Locks = Database.GetCollection<LockDocument>(CollectionName<LockDocument>());
            Batches = Database.GetCollection<BatchDocument>(CollectionName<BatchDocument>());
            BatchItems = Database.GetCollection<BatchItemDocument>(CollectionName<BatchItemDocument>());
            Sagas = Database.GetCollection<SagaDocument>(CollectionName<SagaDocument>());
            SagaSteps = Database.GetCollection<SagaStepDocument>(CollectionName<SagaStepDocument>());
        }

        public MongoBackendWorkOptions Options { get; }

        public IMongoClient Client { get; }

        public IMongoDatabase Database { get; }

        public IMongoCollection<WorkDocument> Works { get; }
        public IMongoCollection<StateDocument> States { get; }
        public IMongoCollection<QueueDocument> Queues { get; }
        public IMongoCollection<CacheSetDocument> CacheSets { get; }
        public IMongoCollection<RecurringDocument> Recurrings { get; }
        public IMongoCollection<ServerDocument> Servers { get; }
        public IMongoCollection<LockDocument> Locks { get; }
        public IMongoCollection<BatchDocument> Batches { get; }
        public IMongoCollection<BatchItemDocument> BatchItems { get; }
        public IMongoCollection<SagaDocument> Sagas { get; }
        public IMongoCollection<SagaStepDocument> SagaSteps { get; }

        /// <summary>
        /// 默认按乐观假设（支持事务）。真实结论在 <see cref="DetectTransactionsAsync"/> 探测后确定，
        /// 且提交路径还留了一道运行期兜底（遇到"单机不支持事务"的错误会降级重放）。
        /// </summary>
        public bool SupportsTransactions { get; private set; } = true;

        public string CollectionName<T>()
        {
            return $"{Options.CollectionPrefix}{typeof(T).Name.Replace("Document", string.Empty)}";
        }

        /// <summary>
        /// 提交路径在运行期才确认"不支持事务"时（探测阶段集群描述仍是 Unknown）调用，
        /// 把结论固化下来，后续提交直接走无事务路径，不再每次都白试一遍。
        /// </summary>
        public void MarkTransactionsUnsupported()
        {
            SupportsTransactions = false;
        }

        /// <summary>
        /// 探测部署形态是否支持多文档事务。必须先发生一次真实命令，集群描述才会被填充。
        /// </summary>
        public async Task<bool> DetectTransactionsAsync(CancellationToken cancellationToken = default)
        {
            // ping 既是连通性检查，也让 Cluster.Description 从 Unknown 变成真实形态
            await Database.RunCommandAsync(
                new BsonDocumentCommand<BsonDocument>(new BsonDocument("ping", 1)),
                cancellationToken: cancellationToken);

            var type = Client.Cluster.Description.Type;

            // Unknown / LoadBalanced 不做悲观判定：让提交路径的兜底逻辑去处理
            SupportsTransactions = type != ClusterType.Standalone;

            if (!SupportsTransactions)
            {
                _logger?.LogWarning(
                    "Mongodb 部署形态为 standalone，不支持多文档事务：IWorkStorage 的提交将退化为逐条写入，原子性无保证。生产请使用副本集（单成员副本集亦可）。");
            }

            return SupportsTransactions;
        }

        /// <summary>
        /// 幂等建索引：与 Sql 迁移中的索引口径一一对应。
        /// Mongodb 的 CreateIndexes 对"已存在且定义相同"的索引是幂等的，多节点并发调用也不会互相冲突。
        /// </summary>
        public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _indexesEnsured, 1) != Helper.Zero)
            {
                return;
            }

            // 监控面板按任务名检索
            await CreateIndexAsync(Works, Builders<WorkDocument>.IndexKeys.Ascending(t => t.Name));

            // 取"最后一条状态"：按工作 Id 分组后按时间倒序取首条
            await CreateIndexAsync(States, Builders<StateDocument>.IndexKeys
                .Ascending(t => t.WorkId)
                .Descending(t => t.CreateUtcTime));
            await CreateIndexAsync(States, Builders<StateDocument>.IndexKeys
                .Ascending(t => t.Name)
                .Ascending(t => t.CreateUtcTime));

            // 出队扫描：按队列 + 可见性时刻过滤
            await CreateIndexAsync(Queues, Builders<QueueDocument>.IndexKeys
                .Ascending(t => t.Queue)
                .Ascending(t => t.FetchUtcTime));

            // (Type, Value) 唯一：重复登记直接失败并回滚事务，故"重新登记"必须先删后加
            await CreateIndexAsync(CacheSets, Builders<CacheSetDocument>.IndexKeys
                    .Ascending(t => t.Type)
                    .Ascending(t => t.Value),
                unique: true);
            await CreateIndexAsync(CacheSets, Builders<CacheSetDocument>.IndexKeys.Ascending(t => t.Score));

            await CreateIndexAsync(BatchItems, Builders<BatchItemDocument>.IndexKeys.Ascending(t => t.BatchId));

            // 业务关联键唯一：同一业务重复启动返回既有 saga
            await CreateIndexAsync(Sagas, Builders<SagaDocument>.IndexKeys.Ascending(t => t.CorrelationId), unique: true);
            // 超时扫描按 (Status, CreateUtcTime) 过滤
            await CreateIndexAsync(Sagas, Builders<SagaDocument>.IndexKeys
                .Ascending(t => t.Status)
                .Ascending(t => t.CreateUtcTime));

            // 步骤按序号取用（游标定位、串行遍历），(SagaId, Index) 唯一顺带保证序号不重复
            await CreateIndexAsync(SagaSteps, Builders<SagaStepDocument>.IndexKeys
                    .Ascending(t => t.SagaId)
                    .Ascending(t => t.Index),
                unique: true);
        }

        private async Task CreateIndexAsync<T>(IMongoCollection<T> collection, IndexKeysDefinition<T> keys, bool unique = false)
        {
            var model = new CreateIndexModel<T>(keys, new CreateIndexOptions { Unique = unique });

            try
            {
                await collection.Indexes.CreateOneAsync(model);
            }
            catch (MongoCommandException ex) when (ex.CodeName != null && ex.CodeName.Contains("IndexOptionsConflict", StringComparison.OrdinalIgnoreCase))
            {
                // 索引已存在但定义不同：不阻塞启动（老库升级场景），仅记警告
                _logger?.LogWarning($"Create index on {collection.CollectionNamespace.CollectionName} conflicted: {ex.Message}");
            }
        }
    }
}
