using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Mongodb.Documents;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Sagas;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using MongoDB.Bson;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Mongodb
{
    /// <summary>
    /// Mongodb 版查询。与 Sql 版一一对应；时间比较可以直接用 BSON Date 比较
    /// （文档侧统一按 UTC 序列化），不再需要 SQLite 那套"格式化成文本再比"的绕法。
    /// </summary>
    public class WorkQuery : IWorkQuery
    {
        private readonly MongoBackendWorkContext _context;

        public WorkQuery(MongoBackendWorkContext context)
        {
            Helper.CheckArgumentNull(context, nameof(context));

            _context = context;
        }

        public async Task<bool> CheckCacheSet(string name, string value)
        {
            var filter = Builders<CacheSetDocument>.Filter.And(
                Builders<CacheSetDocument>.Filter.Eq(t => t.Type, name),
                Builders<CacheSetDocument>.Filter.Eq(t => t.Value, value));

            var count = await _context.CacheSets.CountDocumentsAsync(filter);

            return count > Helper.Zero;
        }

        public async Task<List<string>> GetCacheSet(string name, long startTime, long endTime, int batchSize)
        {
            // 与 Sql 版不同：这里按约定尊重 batchSize（每轮扫描上限，#22 的口径）。
            // 上限内未取到的条目下一轮继续扫，不会漏。
            var filter = Builders<CacheSetDocument>.Filter.And(
                Builders<CacheSetDocument>.Filter.Eq(t => t.Type, name),
                Builders<CacheSetDocument>.Filter.Gte(t => t.Score, startTime),
                Builders<CacheSetDocument>.Filter.Lt(t => t.Score, endTime));

            return await _context.CacheSets
                .Find(filter)
                .SortBy(t => t.Score)
                .Limit(batchSize)
                .Project(t => t.Value)
                .ToListAsync();
        }

        public async Task<bool> CheckWorkExist(string workId)
        {
            var count = await _context.Works.CountDocumentsAsync(Builders<WorkDocument>.Filter.Eq(t => t.Id, workId));

            return count > Helper.Zero;
        }

        public async Task<int> CountState(string wordId, string stateName)
        {
            var filter = Builders<StateDocument>.Filter.And(
                Builders<StateDocument>.Filter.Eq(t => t.WorkId, wordId),
                Builders<StateDocument>.Filter.Eq(t => t.Name, stateName));

            var count = await _context.States.CountDocumentsAsync(filter);

            return (int)count;
        }

        /// <summary>
        /// 查询最后状态为 processing 且早于截止时间的工作 Id（卡死任务恢复）。
        /// <para>
        /// 用聚合在库侧完成"每个工作只取最后一条状态"：
        /// 先按 (WorkId, CreateUtcTime 倒序) 排序，再按 WorkId 分组取首条，最后过滤"processing 且超时"。
        /// 等价 Sql 版的 <c>NOT EXISTS(同工作更晚的状态)</c>，但只需一次往返。
        /// </para>
        /// </summary>
        public async Task<List<string>> GetStaleProcessingWorkIds(DateTime staleBeforeUtcTime, int batchSize)
        {
            var pipeline = new[]
            {
                new BsonDocument("$sort", new BsonDocument
                {
                    { "WorkId", 1 },
                    { "CreateUtcTime", -1 },
                    // 时间戳相同时必须有确定性 tie-break：否则"最后一条状态"在两次查询间
                    // 可能选中不同行，导致同一 workId 反复进出卡死集合（与 Sql 版口径一致）。
                    { "_id", -1 }
                }),
                new BsonDocument("$group", new BsonDocument
                {
                    { "_id", "$WorkId" },
                    { "Name", new BsonDocument("$first", "$Name") },
                    { "CreateUtcTime", new BsonDocument("$first", "$CreateUtcTime") }
                }),
                new BsonDocument("$match", new BsonDocument
                {
                    { "Name", ProcessingWorkState.Name },
                    { "CreateUtcTime", new BsonDocument("$lt", staleBeforeUtcTime) }
                }),
                new BsonDocument("$limit", batchSize)
            };

            var docs = await _context.States.Aggregate<BsonDocument>(pipeline).ToListAsync();

            return docs.Select(t => t["_id"].AsString).ToList();
        }

        public async Task<RecurringWork?> GetRecurringWorkById(string id)
        {
            var doc = await _context.Recurrings
                .Find(Builders<RecurringDocument>.Filter.Eq(t => t.Id, id))
                .FirstOrDefaultAsync();

            if (doc == null)
            {
                return null;
            }

            return new RecurringWork(id)
            {
                Name = doc.Name,
                Cron = doc.Cron,
                WorkContent = doc.JsonContent,
                Queue = doc.Queue,
                TimeZone = doc.TimeZone,
                CreateUtcTime = doc.CreateUtcTime,
                NextExecuteUtcTime = doc.NextExecutUtcTime,
                LastExecuteUtcTime = doc.LastExecutUtcTime,
                LastWorkId = doc.LastWorkId,
                RetryCount = doc.RetryCount,
                ErrorInfo = doc.ErrorInfo,
                Paused = doc.Paused
            };
        }

        public async Task<WorkData?> GetWorkData(string workId)
        {
            var doc = await _context.Works
                .Find(Builders<WorkDocument>.Filter.Eq(t => t.Id, workId))
                .FirstOrDefaultAsync();

            if (doc == null)
            {
                return null;
            }

            return new WorkData
            {
                WorkId = workId,
                Name = doc.Name,
                Content = Helper.Deserialize<WorkloadContent>(doc.InvocationJson),
                StateName = doc.StateName,
                Parameters = Helper.Deserialize<Dictionary<string, string>>(doc.ParametersJson),
                CreatedUctTime = doc.CreateUtcTime,
                ExpireUtcTime = doc.ExpireUtcTime
            };
        }

        public async Task<IWorkState?> GetLastState(string wordId)
        {
            var doc = await _context.States
                .Find(Builders<StateDocument>.Filter.Eq(t => t.WorkId, wordId))
                .SortByDescending(t => t.CreateUtcTime)
                .FirstOrDefaultAsync();

            if (doc == null)
            {
                return null;
            }

            return doc.ToState();
        }

        public async Task<BaseBatchWork?> GetBatchById(string batchId)
        {
            var doc = await _context.Batches
                .Find(Builders<BatchDocument>.Filter.Eq(t => t.BatchId, batchId))
                .FirstOrDefaultAsync();

            if (doc == null)
            {
                return null;
            }

            return new BaseBatchWork
            {
                BatchId = doc.BatchId,
                Name = doc.Name,
                ParentBatchId = doc.ParentBatchId,
                Message = doc.Message,
                Status = doc.Status,
                RetryCount = doc.RetryCount,
                Count = doc.Count,
                DealCount = doc.DealCount,
                ExpireUtcTime = doc.ExpireUtcTime
            };
        }

        public async Task<BatchWork?> GetFullBatchById(string batchId)
        {
            var doc = await _context.Batches
                .Find(Builders<BatchDocument>.Filter.Eq(t => t.BatchId, batchId))
                .FirstOrDefaultAsync();

            if (doc == null)
            {
                return null;
            }

            var items = await _context.BatchItems
                .Find(Builders<BatchItemDocument>.Filter.Eq(t => t.BatchId, batchId))
                .SortBy(t => t.CreateUtcTime)
                .ThenBy(t => t.Id)
                .ToListAsync();

            return new BatchWork
            {
                BatchId = doc.BatchId,
                Name = doc.Name,
                ParentBatchId = doc.ParentBatchId,
                Message = doc.Message,
                Status = doc.Status,
                RetryCount = doc.RetryCount,
                Count = doc.Count,
                DealCount = doc.DealCount,
                ExpireUtcTime = doc.ExpireUtcTime,
                Items = items.Select(t => t.ToItem()).ToList()
            };
        }

        public async Task<BatchItem?> GetBatchItem(string batchId, string itemId)
        {
            var filter = Builders<BatchItemDocument>.Filter.And(
                Builders<BatchItemDocument>.Filter.Eq(t => t.BatchId, batchId),
                Builders<BatchItemDocument>.Filter.Eq(t => t.Id, itemId));

            var doc = await _context.BatchItems.Find(filter).FirstOrDefaultAsync();

            return doc?.ToItem();
        }

        public async Task<List<BatchItem>> GetBatchItems(List<BatchStatus> statuses, string batchId, int startIdx, int batchSize)
        {
            var filter = Builders<BatchItemDocument>.Filter.And(
                Builders<BatchItemDocument>.Filter.Eq(t => t.BatchId, batchId),
                Builders<BatchItemDocument>.Filter.In(t => t.Status, statuses));

            var docs = await _context.BatchItems
                .Find(filter)
                .SortBy(t => t.CreateUtcTime)
                .ThenBy(t => t.Id)
                .Skip(startIdx)
                .Limit(batchSize)
                .ToListAsync();

            return docs.Select(t => t.ToItem()).ToList();
        }

        public async Task<List<BatchItem>> GetBatchItems(string batchId, int startIdx, int batchSize)
        {
            var docs = await _context.BatchItems
                .Find(Builders<BatchItemDocument>.Filter.Eq(t => t.BatchId, batchId))
                .SortBy(t => t.CreateUtcTime)
                .ThenBy(t => t.Id)
                .Skip(startIdx)
                .Limit(batchSize)
                .ToListAsync();

            return docs.Select(t => t.ToItem()).ToList();
        }

        public async Task<int> CountBatchItems(string batchId, List<BatchStatus> statuses)
        {
            var filter = Builders<BatchItemDocument>.Filter.And(
                Builders<BatchItemDocument>.Filter.Eq(t => t.BatchId, batchId),
                Builders<BatchItemDocument>.Filter.In(t => t.Status, statuses));

            var count = await _context.BatchItems.CountDocumentsAsync(filter);

            return (int)count;
        }

        #region Saga

        public async Task<SagaInstance?> GetSagaById(string sagaId)
        {
            var doc = await _context.Sagas
                .Find(Builders<SagaDocument>.Filter.Eq(t => t.SagaId, sagaId))
                .FirstOrDefaultAsync();

            return doc?.ToSaga();
        }

        public async Task<SagaInstance?> GetSagaByCorrelationId(string correlationId)
        {
            var doc = await _context.Sagas
                .Find(Builders<SagaDocument>.Filter.Eq(t => t.CorrelationId, correlationId))
                .FirstOrDefaultAsync();

            return doc?.ToSaga();
        }

        /// <summary>步骤必须按序号升序返回：游标按序号定位步骤，顺序错乱会推进到错误的步骤。</summary>
        public async Task<List<SagaStep>> GetSagaSteps(string sagaId)
        {
            var docs = await _context.SagaSteps
                .Find(Builders<SagaStepDocument>.Filter.Eq(t => t.SagaId, sagaId))
                .SortBy(t => t.Index)
                .ToListAsync();

            return docs.Select(t => t.ToStep()).ToList();
        }

        public async Task<List<string>> GetTimeoutSagaIds(DateTime startedBeforeUtcTime, int batchSize)
        {
            var filter = Builders<SagaDocument>.Filter.And(
                Builders<SagaDocument>.Filter.Eq(t => t.Status, SagaStatus.Running),
                Builders<SagaDocument>.Filter.Lt(t => t.CreateUtcTime, startedBeforeUtcTime));

            return await _context.Sagas
                .Find(filter)
                .SortBy(t => t.CreateUtcTime)
                .Limit(batchSize)
                .Project(t => t.SagaId)
                .ToListAsync();
        }

        #endregion
    }
}
