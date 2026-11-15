using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Mongodb;
using Gadamaxi.BackendWork.Mongodb.Documents;
using Gadamaxi.BackendWork.Sql;
using Gadamaxi.BackendWork.Sql.Tables;
using Gadamaxi.BackendWork.Works.States;
using LinqToDB;
using LinqToDB.Async;
using MongoDB.Driver;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 测试用的后端无关数据访问层：把"直接查库/种库"的操作抽象出来，
    /// Sql 用 linq2db、Mongodb 用驱动实现同一套词汇。测试代码只面对本接口，
    /// 由此同一份测试可以在两种存储后端下运行。
    /// </summary>
    public interface ITestDataAccess
    {
        // ---- Work ----
        Task<string?> GetWorkNameAsync(string workId);
        Task<bool> WorkExistsAsync(string workId);
        Task<int> CountWorksAsync();
        Task<List<string>> GetAllWorkNamesAsync();

        /// <summary>直接种一条工作行（绕过框架 API，用于构造"过期终态"等框架自身造不出来的状态）。</summary>
        Task SeedWorkAsync(string id, string stateName, DateTime? expireUtcTime, string? name = null);

        // ---- State ----
        Task SeedStateAsync(string workId, string stateName);

        /// <summary>按时间升序读一个工作的全部状态名（状态链，诊断用）。</summary>
        Task<List<string>> GetStateChainAsync(string workId);

        /// <summary>读指定工作、指定状态名那一行的 JsonData（如 AwaitingWorkState 的父任务信息）。</summary>
        Task<string?> GetStateDataAsync(string workId, string stateName);

        // ---- Queue ----
        Task<bool> QueueRowExistsAsync(string workId);
        Task<string?> GetQueueNameAsync(string workId);
        Task<string?> GetQueueFetchedByAsync(string workId);
        Task DeleteQueueRowAsync(string workId);
        Task AgeQueueRowAsync(string workId, TimeSpan age);
        Task<List<TestQueueRow>> GetAllQueueRowsAsync();
        Task SeedQueueAsync(string workId, string queue);

        // ---- CacheSet ----
        Task<List<TestCacheSetRow>> GetCacheSetRowsAsync();

        // ---- Server ----
        Task SeedServerAsync(string name, DateTime heartbeatUtcTime);
        Task<bool> ServerExistsAsync(string name);

        // ---- Recurring ----
        Task<int> CountRecurringsAsync();

        // ---- Batch ----
        Task<bool> BatchExistsAsync(string batchId);
        Task<bool> BatchItemExistsAsync(string itemId);
        Task<string?> GetBatchNameAsync(string batchId);
        Task<List<string>> GetBatchItemNamesAsync(string batchId);
        Task SeedBatchAsync(string id, BatchStatus status, DateTime expireUtcTime);
        Task SeedBatchItemAsync(string id, string batchId);

        // ---- Saga ----
        Task<int> CountSagasAsync(string? correlationId = null);
        Task<int> CountSagaStepsAsync(string sagaId);
        Task AgeSagaAsync(string sagaId, TimeSpan age);
        Task ExpireSagaAsync(string sagaId);
    }

    /// <summary>队列行快照（后端无关）。</summary>
    public sealed record TestQueueRow(string WorkId, string Queue, DateTime? FetchUtcTime);

    /// <summary>调度缓存行快照（后端无关）。</summary>
    public sealed record TestCacheSetRow(string Type, double Score, string Value, DateTime? ExpireUtcTime);

    /// <summary>Sql 实现：与原各测试里的裸 linq2db 查询逐字等价。</summary>
    public sealed class SqlTestDataAccess : ITestDataAccess
    {
        /// <summary>手动种队列行时用的自增序号（避开框架已插入的自增 Id 区间）。</summary>
        private static long _seedQueueId;

        private readonly DataOptions _dataOptions;

        /// <summary>
        /// 目标库是否 SqlServer。用于规避 IDENTITY 列差异：SqlServer 的 Id 是
        /// <c>bigint IDENTITY</c>，显式插入会报 "Cannot insert explicit value for identity column"；
        /// SQLite 的 INTEGER PRIMARY KEY 则允许显式赋值。
        /// </summary>
        private readonly bool _isSqlServer;

        public SqlTestDataAccess(DataOptions dataOptions)
        {
            _dataOptions = dataOptions;

            using var probe = new DBConnection(dataOptions);
            _isSqlServer = probe.DataProvider != null
                && probe.DataProvider.Name.StartsWith(ProviderName.SqlServer, StringComparison.OrdinalIgnoreCase);
        }

        private DBConnection Open() => new DBConnection(_dataOptions);

        public async Task<string?> GetWorkNameAsync(string workId)
        {
            using var db = Open();
            return await db.WorkTable
                .Where(t => t.Id == workId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync();
        }

        public async Task<bool> WorkExistsAsync(string workId)
        {
            using var db = Open();
            return await db.WorkTable.AnyAsync(t => t.Id == workId);
        }

        public async Task<int> CountWorksAsync()
        {
            using var db = Open();
            return await db.WorkTable.CountAsync();
        }

        public async Task<List<string>> GetAllWorkNamesAsync()
        {
            using var db = Open();
            return await db.WorkTable.Select(t => t.Name).ToListAsync();
        }

        public async Task SeedWorkAsync(string id, string stateName, DateTime? expireUtcTime, string? name = null)
        {
            using var db = Open();
            await db.WorkTable
                .Value(t => t.Id, id)
                .Value(t => t.Name, name ?? id)
                .Value(t => t.StateId, string.Empty)
                .Value(t => t.StateName, stateName)
                .Value(t => t.InvocationJson, "{}")
                .Value(t => t.ParametersJson, "{}")
                .Value(t => t.CreateUtcTime, Helper.CurrentUtcTime())
                .Value(t => t.ExpireUtcTime, expireUtcTime)
                .InsertAsync();
        }

        public async Task SeedStateAsync(string workId, string stateName)
        {
            using var db = Open();
            await db.StateTable
                .Value(t => t.Id, Helper.NewId())
                .Value(t => t.Name, stateName)
                .Value(t => t.WorkId, workId)
                .Value(t => t.Info, string.Empty)
                .Value(t => t.CreateUtcTime, Helper.CurrentUtcTime())
                .Value(t => t.JsonData, "{}")
                .InsertAsync();
        }

        public async Task<List<string>> GetStateChainAsync(string workId)
        {
            using var db = Open();
            return await db.StateTable
                .Where(t => t.WorkId == workId)
                .OrderBy(t => t.CreateUtcTime)
                .Select(t => t.Name)
                .ToListAsync();
        }

        public async Task<string?> GetStateDataAsync(string workId, string stateName)
        {
            using var db = Open();
            return await db.StateTable
                .Where(t => t.WorkId == workId && t.Name == stateName)
                .Select(t => t.JsonData)
                .FirstOrDefaultAsync();
        }

        public async Task<bool> QueueRowExistsAsync(string workId)
        {
            using var db = Open();
            return await db.QueueTable.AnyAsync(t => t.WorkId == workId);
        }

        public async Task<string?> GetQueueNameAsync(string workId)
        {
            using var db = Open();
            return await db.QueueTable
                .Where(t => t.WorkId == workId)
                .Select(t => t.Queue)
                .FirstOrDefaultAsync();
        }

        public async Task<string?> GetQueueFetchedByAsync(string workId)
        {
            using var db = Open();
            return await db.QueueTable
                .Where(t => t.WorkId == workId)
                .Select(t => t.FetchedBy)
                .FirstOrDefaultAsync();
        }

        public async Task DeleteQueueRowAsync(string workId)
        {
            using var db = Open();
            await db.QueueTable.Where(t => t.WorkId == workId).DeleteAsync();
        }

        public async Task AgeQueueRowAsync(string workId, TimeSpan age)
        {
            using var db = Open();
            await db.QueueTable
                .Where(t => t.WorkId == workId)
                .Set(t => t.FetchUtcTime, Helper.CurrentUtcTime() - age)
                .UpdateAsync();
        }

        public async Task<List<TestQueueRow>> GetAllQueueRowsAsync()
        {
            using var db = Open();
            var rows = await db.QueueTable
                .OrderBy(t => t.Id)
                .Select(t => new TestQueueRow(t.WorkId, t.Queue, t.FetchUtcTime))
                .ToListAsync();
            return rows;
        }

        public async Task SeedQueueAsync(string workId, string queue)
        {
            using var db = Open();
            var row = db.QueueTable
                .Value(t => t.WorkId, workId)
                .Value(t => t.Queue, queue);

            // SqlServer 的 Id 为 IDENTITY：交给数据库自增（必然排在既有行之后，与显式高位序号等效）；
            // SQLite 无 IDENTITY 语义，显式给一个大序号以避开框架自增区间。
            if (!_isSqlServer)
            {
                row = row.Value(t => t.Id, Interlocked.Increment(ref _seedQueueId) + 1_000_000);
            }

            await row.InsertAsync();
        }

        public async Task<List<TestCacheSetRow>> GetCacheSetRowsAsync()
        {
            using var db = Open();
            var rows = await db.CacheSetTable
                .Select(t => new TestCacheSetRow(t.Type, t.Score, t.Value, t.ExpireUtcTime))
                .ToListAsync();
            return rows;
        }

        public async Task SeedServerAsync(string name, DateTime heartbeatUtcTime)
        {
            using var db = Open();
            await db.ServerTable
                .Value(t => t.ServerName, name)
                .Value(t => t.CreateUtcTime, heartbeatUtcTime)
                .Value(t => t.LastHeartbeatUtcTime, heartbeatUtcTime)
                .Value(t => t.JsonData, "{}")
                .InsertAsync();
        }

        public async Task<bool> ServerExistsAsync(string name)
        {
            using var db = Open();
            return await db.ServerTable.AnyAsync(t => t.ServerName == name);
        }

        public async Task<int> CountRecurringsAsync()
        {
            using var db = Open();
            return await db.RecurringTable.CountAsync();
        }

        public async Task<bool> BatchExistsAsync(string batchId)
        {
            using var db = Open();
            return await db.BatchTable.AnyAsync(t => t.BatchId == batchId);
        }

        public async Task<bool> BatchItemExistsAsync(string itemId)
        {
            using var db = Open();
            return await db.BatchItemTable.AnyAsync(t => t.Id == itemId);
        }

        public async Task<string?> GetBatchNameAsync(string batchId)
        {
            using var db = Open();
            return await db.BatchTable
                .Where(t => t.BatchId == batchId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync();
        }

        public async Task<List<string>> GetBatchItemNamesAsync(string batchId)
        {
            using var db = Open();
            return await db.BatchItemTable
                .Where(t => t.BatchId == batchId)
                .OrderBy(t => t.Id)
                .Select(t => t.Name)
                .ToListAsync();
        }

        public async Task SeedBatchAsync(string id, BatchStatus status, DateTime expireUtcTime)
        {
            using var db = Open();
            await db.BatchTable
                .Value(t => t.BatchId, id)
                .Value(t => t.Name, id)
                .Value(t => t.ParentBatchId, string.Empty)
                .Value(t => t.Status, status)
                .Value(t => t.Message, string.Empty)
                .Value(t => t.RetryCount, 0)
                .Value(t => t.Count, 1)
                .Value(t => t.DealCount, 1)
                .Value(t => t.ExpireUtcTime, expireUtcTime)
                .Value(t => t.CreateUtcTime, Helper.CurrentUtcTime())
                .Value(t => t.UpdateUtcTime, Helper.CurrentUtcTime())
                .InsertAsync();
        }

        public async Task SeedBatchItemAsync(string id, string batchId)
        {
            using var db = Open();
            await db.BatchItemTable
                .Value(t => t.Id, id)
                .Value(t => t.Name, id)
                .Value(t => t.BatchId, batchId)
                .Value(t => t.WorkId, string.Empty)
                .Value(t => t.Status, BatchStatus.Success)
                .Value(t => t.Message, string.Empty)
                .Value(t => t.WorkJson, "{}")
                .Value(t => t.DataJson, "{}")
                .Value(t => t.DataType, string.Empty)
                .Value(t => t.CreateUtcTime, Helper.CurrentUtcTime())
                .Value(t => t.UpdateUtcTime, Helper.CurrentUtcTime())
                .InsertAsync();
        }

        public async Task<int> CountSagasAsync(string? correlationId = null)
        {
            using var db = Open();
            return correlationId == null
                ? await db.SagaTable.CountAsync()
                : await db.SagaTable.CountAsync(t => t.CorrelationId == correlationId);
        }

        public async Task<int> CountSagaStepsAsync(string sagaId)
        {
            using var db = Open();
            return await db.SagaStepTable.CountAsync(t => t.SagaId == sagaId);
        }

        public async Task AgeSagaAsync(string sagaId, TimeSpan age)
        {
            using var db = Open();
            await db.SagaTable
                .Where(t => t.SagaId == sagaId)
                .Set(t => t.CreateUtcTime, Helper.CurrentUtcTime() - age)
                .UpdateAsync();
        }

        public async Task ExpireSagaAsync(string sagaId)
        {
            using var db = Open();
            await db.SagaTable
                .Where(t => t.SagaId == sagaId)
                .Set(t => t.ExpireUtcTime, Helper.CurrentUtcTime() - TimeSpan.FromDays(1))
                .UpdateAsync();
        }
    }

    /// <summary>Mongodb 实现：与 Sql 版语义一一对应（排序键用 NewId 顺序 GUID / ObjectId 保证确定性）。</summary>
    public sealed class MongoTestDataAccess : ITestDataAccess
    {
        private readonly MongoBackendWorkContext _ctx;

        public MongoTestDataAccess(MongoBackendWorkContext context)
        {
            _ctx = context;
        }

        public async Task<string?> GetWorkNameAsync(string workId)
        {
            var doc = await _ctx.Works.Find(d => d.Id == workId).FirstOrDefaultAsync();
            return doc?.Name;
        }

        public async Task<bool> WorkExistsAsync(string workId)
            => await _ctx.Works.Find(d => d.Id == workId).AnyAsync();

        public async Task<int> CountWorksAsync()
            => (int)await _ctx.Works.CountDocumentsAsync(FilterDefinition<WorkDocument>.Empty);

        public async Task<List<string>> GetAllWorkNamesAsync()
            => await _ctx.Works.Find(FilterDefinition<WorkDocument>.Empty).Project(d => d.Name).ToListAsync();

        public async Task SeedWorkAsync(string id, string stateName, DateTime? expireUtcTime, string? name = null)
        {
            await _ctx.Works.InsertOneAsync(new WorkDocument
            {
                Id = id,
                Name = name ?? id,
                StateId = string.Empty,
                StateName = stateName,
                InvocationJson = "{}",
                ParametersJson = "{}",
                CreateUtcTime = Helper.CurrentUtcTime(),
                ExpireUtcTime = expireUtcTime,
            });
        }

        public async Task SeedStateAsync(string workId, string stateName)
        {
            await _ctx.States.InsertOneAsync(new StateDocument
            {
                Id = Helper.NewId(),
                Name = stateName,
                WorkId = workId,
                Info = string.Empty,
                CreateUtcTime = Helper.CurrentUtcTime(),
                JsonData = "{}",
            });
        }

        public Task<List<string>> GetStateChainAsync(string workId)
            => _ctx.States.Find(d => d.WorkId == workId)
                .SortBy(d => d.CreateUtcTime)
                .Project(d => d.Name)
                .ToListAsync();

        public async Task<string?> GetStateDataAsync(string workId, string stateName)
        {
            var doc = await _ctx.States
                .Find(d => d.WorkId == workId && d.Name == stateName)
                .FirstOrDefaultAsync();
            return doc?.JsonData;
        }

        public async Task<bool> QueueRowExistsAsync(string workId)
            => await _ctx.Queues.Find(d => d.WorkId == workId).AnyAsync();

        public async Task<string?> GetQueueNameAsync(string workId)
        {
            var doc = await _ctx.Queues.Find(d => d.WorkId == workId).FirstOrDefaultAsync();
            return doc?.Queue;
        }

        public async Task<string?> GetQueueFetchedByAsync(string workId)
        {
            var doc = await _ctx.Queues.Find(d => d.WorkId == workId).FirstOrDefaultAsync();
            return doc?.FetchedBy;
        }

        public Task DeleteQueueRowAsync(string workId)
            => _ctx.Queues.DeleteManyAsync(d => d.WorkId == workId);

        public Task AgeQueueRowAsync(string workId, TimeSpan age)
            => _ctx.Queues.UpdateManyAsync(
                d => d.WorkId == workId,
                Builders<QueueDocument>.Update.Set(d => d.FetchUtcTime, Helper.CurrentUtcTime() - age));

        public async Task<List<TestQueueRow>> GetAllQueueRowsAsync()
        {
            var docs = await _ctx.Queues
                .Find(FilterDefinition<QueueDocument>.Empty)
                .SortBy(d => d.Id) // ObjectId 自带时间递增：与 Sql 按 Id 排序同为写入序
                .ToListAsync();
            return docs.Select(d => new TestQueueRow(d.WorkId, d.Queue, d.FetchUtcTime)).ToList();
        }

        public async Task SeedQueueAsync(string workId, string queue)
        {
            await _ctx.Queues.InsertOneAsync(new QueueDocument
            {
                Id = MongoDB.Bson.ObjectId.GenerateNewId(),
                WorkId = workId,
                Queue = queue,
            });
        }

        public async Task<List<TestCacheSetRow>> GetCacheSetRowsAsync()
        {
            var docs = await _ctx.CacheSets.Find(FilterDefinition<CacheSetDocument>.Empty).ToListAsync();
            return docs.Select(d => new TestCacheSetRow(d.Type, d.Score, d.Value, d.ExpireUtcTime)).ToList();
        }

        public async Task SeedServerAsync(string name, DateTime heartbeatUtcTime)
        {
            await _ctx.Servers.InsertOneAsync(new ServerDocument
            {
                ServerName = name,
                CreateUtcTime = heartbeatUtcTime,
                LastHeartbeatUtcTime = heartbeatUtcTime,
                JsonData = "{}",
            });
        }

        public async Task<bool> ServerExistsAsync(string name)
            => await _ctx.Servers.Find(d => d.ServerName == name).AnyAsync();

        public Task<int> CountRecurringsAsync()
            => CountAsync(_ctx.Recurrings, FilterDefinition<RecurringDocument>.Empty);

        public async Task<bool> BatchExistsAsync(string batchId)
            => await _ctx.Batches.Find(d => d.BatchId == batchId).AnyAsync();

        public async Task<bool> BatchItemExistsAsync(string itemId)
            => await _ctx.BatchItems.Find(d => d.Id == itemId).AnyAsync();

        public async Task<string?> GetBatchNameAsync(string batchId)
        {
            var doc = await _ctx.Batches.Find(d => d.BatchId == batchId).FirstOrDefaultAsync();
            return doc?.Name;
        }

        public Task<List<string>> GetBatchItemNamesAsync(string batchId)
            => _ctx.BatchItems.Find(d => d.BatchId == batchId)
                .SortBy(d => d.Id) // Helper.NewId 顺序 GUID：字符串序即生成序（与 Sql 自增 Id 等价）
                .Project(d => d.Name)
                .ToListAsync();

        public async Task SeedBatchAsync(string id, BatchStatus status, DateTime expireUtcTime)
        {
            await _ctx.Batches.InsertOneAsync(new BatchDocument
            {
                BatchId = id,
                Name = id,
                ParentBatchId = string.Empty,
                Status = status,
                Message = string.Empty,
                RetryCount = 0,
                Count = 1,
                DealCount = 1,
                ExpireUtcTime = expireUtcTime,
                CreateUtcTime = Helper.CurrentUtcTime(),
                UpdateUtcTime = Helper.CurrentUtcTime(),
            });
        }

        public async Task SeedBatchItemAsync(string id, string batchId)
        {
            await _ctx.BatchItems.InsertOneAsync(new BatchItemDocument
            {
                Id = id,
                Name = id,
                BatchId = batchId,
                WorkId = string.Empty,
                Status = BatchStatus.Success,
                Message = string.Empty,
                WorkJson = "{}",
                DataJson = "{}",
                DataType = string.Empty,
                CreateUtcTime = Helper.CurrentUtcTime(),
                UpdateUtcTime = Helper.CurrentUtcTime(),
            });
        }

        public Task<int> CountSagasAsync(string? correlationId = null)
            => CountAsync(_ctx.Sagas,
                correlationId == null
                    ? FilterDefinition<SagaDocument>.Empty
                    : Builders<SagaDocument>.Filter.Eq(d => d.CorrelationId, correlationId));

        public Task<int> CountSagaStepsAsync(string sagaId)
            => CountAsync(_ctx.SagaSteps, Builders<SagaStepDocument>.Filter.Eq(d => d.SagaId, sagaId));

        public Task AgeSagaAsync(string sagaId, TimeSpan age)
            => _ctx.Sagas.UpdateManyAsync(
                d => d.SagaId == sagaId,
                Builders<SagaDocument>.Update.Set(d => d.CreateUtcTime, Helper.CurrentUtcTime() - age));

        public Task ExpireSagaAsync(string sagaId)
            => _ctx.Sagas.UpdateManyAsync(
                d => d.SagaId == sagaId,
                Builders<SagaDocument>.Update.Set(d => d.ExpireUtcTime, Helper.CurrentUtcTime() - TimeSpan.FromDays(1)));

        private static async Task<int> CountAsync<T>(IMongoCollection<T> collection, FilterDefinition<T> filter)
            => (int)await collection.CountDocumentsAsync(filter);
    }
}
