using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Exceptions;
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
    /// Mongodb 版工作存储。
    /// <para>
    /// 与 Sql 版同构：<b>写入只入队，<see cref="Commit"/> 才落库</b>，一次 Commit 内的全部写入在
    /// 同一个 Mongodb 事务中提交。上层语义（状态回写无条件、推进/抢占 CAS、写入与"对外可见"同事务）
    /// 全部保持不变，只是把 SQL 的 WHERE 守卫换成 Mongodb 的 filter 守卫。
    /// </para>
    /// </summary>
    public class WorkStorage : IWorkStorage
    {
        private readonly List<Func<IClientSessionHandle?, Task>> _cache;
        private readonly MongoBackendWorkContext _context;

        public WorkStorage(MongoBackendWorkContext context)
        {
            Helper.CheckArgumentNull(context, nameof(context));

            _context = context;
            _cache = new List<Func<IClientSessionHandle?, Task>>();
        }

        private Task AddTransaction(Func<IClientSessionHandle?, Task> action)
        {
            _cache.Add(action);

            return Task.CompletedTask;
        }

        #region 会话/事务

        public async Task Commit()
        {
            if (_cache.Count == Helper.Zero)
            {
                return;
            }

            if (!_context.SupportsTransactions)
            {
                if (_context.Options.RequireTransactions)
                {
                    throw new BackendException(
                        "Mongodb 部署形态不支持多文档事务（standalone），而当前配置 RequireTransactions=true。请使用副本集，或关闭该开关以接受无事务保证。");
                }

                // 单机部署：退化为逐条写入（原子性无保证，已在探测阶段记警告）
                foreach (var action in _cache)
                {
                    await action(null);
                }

                return;
            }

            try
            {
                using (var session = await _context.Client.StartSessionAsync())
                {
                    session.StartTransaction();

                    try
                    {
                        foreach (var action in _cache)
                        {
                            await action(session);
                        }

                        await session.CommitTransactionAsync();
                    }
                    catch
                    {
                        await session.AbortTransactionAsync();
                        throw;
                    }
                }
            }
            catch (MongoCommandException ex) when (!_context.Options.RequireTransactions && IsTransactionUnsupported(ex))
            {
                // 兜底：拓扑探测时集群描述还是 Unknown（或中途切成单机），到这里才暴露。
                // 记录一次结论后降级重放，避免整个服务起不来。
                _context.MarkTransactionsUnsupported();

                foreach (var action in _cache)
                {
                    await action(null);
                }
            }
        }

        private static bool IsTransactionUnsupported(MongoCommandException ex)
        {
            var message = ex.Message ?? string.Empty;

            return message.IndexOf("Transaction numbers are only allowed", StringComparison.OrdinalIgnoreCase) >= Helper.Zero
                   || message.IndexOf("replica set member or mongos", StringComparison.OrdinalIgnoreCase) >= Helper.Zero;
        }

        public void Dispose()
        {
        }

        #endregion

        #region 工作与状态

        public async Task CreateWork(string workId, Workload work, IDictionary<string, object>? addParameters, DateTime createUtcTime, TimeSpan expireIn, string name)
        {
            var doc = new WorkDocument
            {
                Id = workId,
                Name = name ?? string.Empty,
                StateId = string.Empty,
                StateName = string.Empty,
                InvocationJson = work.Serialize(),
                ParametersJson = Helper.Serialize(addParameters) ?? string.Empty,
                CreateUtcTime = createUtcTime,
                ExpireUtcTime = createUtcTime.Add(expireIn)
            };

            await AddTransaction((session) => InsertOneAsync(_context.Works, session, doc));
        }

        public async Task RemoveWork(string workId)
        {
            await AddTransaction((session) => DeleteManyAsync(_context.Works, session, Builders<WorkDocument>.Filter.Eq(t => t.Id, workId)));
        }

        public async Task SetWorkState(string workId, IWorkState initState)
        {
            var stateId = Helper.NewId();
            var state = new StateDocument
            {
                Id = stateId,
                Name = initState.StateName,
                WorkId = workId,
                Info = initState.Info,
                CreateUtcTime = initState.CreateUtcTime,
                JsonData = Helper.Serialize(initState.GetData()) ?? string.Empty
            };

            await AddTransaction(async (session) =>
            {
                await InsertOneAsync(_context.States, session, state);

                var update = Builders<WorkDocument>.Update
                    .Set(t => t.StateId, stateId)
                    .Set(t => t.StateName, initState.StateName);

                await UpdateOneAsync(_context.Works, session, Builders<WorkDocument>.Filter.Eq(t => t.Id, workId), update);
            });
        }

        public async Task RemoveWorkState(string workId)
        {
            await AddTransaction((session) => DeleteManyAsync(_context.States, session, Builders<StateDocument>.Filter.Eq(t => t.WorkId, workId)));
        }

        /// <summary>
        /// 入队消息与工作行、状态行在同一事务中提交：消息绝不能先于工作行可见，
        /// 否则消费端会读到"工作不存在"而把消息确认掉，任务将被永久丢弃。
        /// </summary>
        public async Task Publish(QueueInfo info)
        {
            var doc = new QueueDocument
            {
                WorkId = info.WorkId,
                Queue = info.QueueName,
                FetchUtcTime = null,
                FetchedBy = null
            };

            await AddTransaction((session) => InsertOneAsync(_context.Queues, session, doc));
        }

        #endregion

        #region 调度缓存

        public async Task AddCacheSet(string key, double score, string data)
        {
            var doc = new CacheSetDocument
            {
                Type = key,
                Score = score,
                Value = data
            };

            // (Type, Value) 唯一索引 → 重复登记会失败并回滚整个事务，与 Sql 的唯一约束行为一致。
            // 因此所有"重新登记"路径必须先 Remove 再 Add。
            await AddTransaction((session) => InsertOneAsync(_context.CacheSets, session, doc));
        }

        public async Task UpdateCacheSet(string key, double score, string data)
        {
            var filter = Builders<CacheSetDocument>.Filter.And(
                Builders<CacheSetDocument>.Filter.Eq(t => t.Type, key),
                Builders<CacheSetDocument>.Filter.Eq(t => t.Value, data));

            await AddTransaction((session) => UpdateOneAsync(_context.CacheSets, session, filter,
                Builders<CacheSetDocument>.Update.Set(t => t.Score, score)));
        }

        public async Task RemoveCacheSet(string name, string data)
        {
            var filter = Builders<CacheSetDocument>.Filter.And(
                Builders<CacheSetDocument>.Filter.Eq(t => t.Type, name),
                Builders<CacheSetDocument>.Filter.Eq(t => t.Value, data));

            await AddTransaction((session) => DeleteManyAsync(_context.CacheSets, session, filter));
        }

        #endregion

        #region 周期任务

        public async Task AddRecurringWork(RecurringWork recurringwork)
        {
            await AddTransaction((session) => InsertOneAsync(_context.Recurrings, session, ToDocument(recurringwork)));
        }

        public async Task UpdateRecurringWork(RecurringWork recurringwork)
        {
            var update = Builders<RecurringDocument>.Update
                .Set(t => t.Name, recurringwork.Name)
                .Set(t => t.Cron, recurringwork.Cron)
                .Set(t => t.JsonContent, recurringwork.WorkContent)
                .Set(t => t.Queue, recurringwork.Queue)
                .Set(t => t.TimeZone, recurringwork.TimeZone)
                .Set(t => t.NextExecutUtcTime, recurringwork.NextExecuteUtcTime)
                .Set(t => t.LastExecutUtcTime, recurringwork.LastExecuteUtcTime)
                .Set(t => t.LastWorkId, recurringwork.LastWorkId)
                .Set(t => t.RetryCount, recurringwork.RetryCount)
                .Set(t => t.ErrorInfo, recurringwork.ErrorInfo)
                .Set(t => t.Paused, recurringwork.Paused);

            await AddTransaction((session) => UpdateOneAsync(_context.Recurrings, session,
                Builders<RecurringDocument>.Filter.Eq(t => t.Id, recurringwork.Id), update));
        }

        /// <summary>
        /// CAS 抢占"本次触发"：filter 里带上期望的 LastExecutUtcTime 版本（NULL 视为同一版本）。
        /// 单行更新在 Mongodb 上是原子的，抢不到的更新 0 匹配 → 抛异常回滚整个触发事务，从而不写重复工作。
        /// <para>
        /// 时间统一截断到<b>毫秒</b>再参与比较：BSON Date 只有毫秒精度，
        /// 若拿内存里带亚毫秒刻度的时间去相等匹配，永远匹配不上（CAS 恒失败）。
        /// 读回的值本就是毫秒精度，截断后与"读回再传"的调用方式结果一致。
        /// </para>
        /// </summary>
        public async Task AdvanceRecurringWork(RecurringWork recurringwork, DateTime? expectedLastExecuteUtcTime)
        {
            var expected = TruncateToMillisecond(expectedLastExecuteUtcTime);
            var lastExecute = TruncateToMillisecond(recurringwork.LastExecuteUtcTime);
            var nextExecute = TruncateToMillisecond(recurringwork.NextExecuteUtcTime);

            var filter = Builders<RecurringDocument>.Filter.And(
                Builders<RecurringDocument>.Filter.Eq(t => t.Id, recurringwork.Id),
                Builders<RecurringDocument>.Filter.Eq(t => t.LastExecutUtcTime, expected));

            var update = Builders<RecurringDocument>.Update
                .Set(t => t.LastExecutUtcTime, lastExecute)
                .Set(t => t.NextExecutUtcTime, nextExecute);

            await AddTransaction(async (session) =>
            {
                var result = await UpdateOneAsync(_context.Recurrings, session, filter, update);

                if (result.MatchedCount <= Helper.Zero)
                {
                    throw new RecurringDuplicateTriggerException(recurringwork.Id);
                }
            });
        }

        public async Task RemoveRecurringWork(string id)
        {
            await AddTransaction((session) => DeleteManyAsync(_context.Recurrings, session, Builders<RecurringDocument>.Filter.Eq(t => t.Id, id)));
        }

        /// <summary>BSON Date 只有毫秒精度：相等比较前必须先把亚毫秒刻度截掉。</summary>
        private static DateTime? TruncateToMillisecond(DateTime? value)
        {
            if (value == null)
            {
                return null;
            }

            var ticks = value.Value.Ticks;

            return new DateTime(ticks - (ticks % TimeSpan.TicksPerMillisecond), value.Value.Kind);
        }

        private static RecurringDocument ToDocument(RecurringWork recurringwork)
        {
            return new RecurringDocument
            {
                Id = recurringwork.Id,
                Name = recurringwork.Name,
                Cron = recurringwork.Cron,
                JsonContent = Helper.Serialize(recurringwork.WorkContent) ?? string.Empty,
                Queue = recurringwork.Queue,
                TimeZone = recurringwork.TimeZone,
                CreateUtcTime = recurringwork.CreateUtcTime,
                NextExecutUtcTime = recurringwork.NextExecuteUtcTime,
                LastExecutUtcTime = recurringwork.LastExecuteUtcTime,
                LastWorkId = recurringwork.LastWorkId,
                RetryCount = recurringwork.RetryCount,
                ErrorInfo = recurringwork.ErrorInfo,
                Paused = recurringwork.Paused
            };
        }

        #endregion

        #region 服务节点

        /// <summary>
        /// 幂等注册：节点名通常来自固定配置，异常关闭后旧记录仍在集合中，
        /// 直接插入会因主键冲突导致宿主无法再次启动。先清理同名残留再插入。
        /// </summary>
        public async Task AddServer(ServerInfo serverInfo)
        {
            var doc = new ServerDocument
            {
                ServerName = serverInfo.ServerName,
                CreateUtcTime = Helper.CurrentUtcTime(),
                LastHeartbeatUtcTime = serverInfo.HeartTime,
                JsonData = Helper.Serialize(serverInfo.Data) ?? string.Empty
            };

            await AddTransaction(async (session) =>
            {
                await DeleteManyAsync(_context.Servers, session, Builders<ServerDocument>.Filter.Eq(t => t.ServerName, serverInfo.ServerName));
                await InsertOneAsync(_context.Servers, session, doc);
            });
        }

        public async Task ServerHeartbeat(string serverName)
        {
            await AddTransaction((session) => UpdateOneAsync(_context.Servers, session,
                Builders<ServerDocument>.Filter.Eq(t => t.ServerName, serverName),
                Builders<ServerDocument>.Update.Set(t => t.LastHeartbeatUtcTime, Helper.CurrentUtcTime())));
        }

        public async Task RemoveServer(DateTime expireTime)
        {
            await AddTransaction((session) => DeleteManyAsync(_context.Servers, session,
                Builders<ServerDocument>.Filter.Lt(t => t.LastHeartbeatUtcTime, expireTime)));
        }

        #endregion

        #region 批量

        public async Task AddBatch(BatchWork batch)
        {
            var now = Helper.CurrentUtcTime();

            var doc = new BatchDocument
            {
                BatchId = batch.BatchId,
                Name = batch.Name ?? string.Empty,
                Status = batch.Status,
                RetryCount = batch.RetryCount,
                Count = batch.Count,
                DealCount = batch.DealCount,
                Message = batch.Message,
                ExpireUtcTime = batch.ExpireUtcTime,
                ParentBatchId = batch.ParentBatchId,
                CreateUtcTime = now,
                UpdateUtcTime = now
            };

            var items = batch.Items.Select(item => new BatchItemDocument
            {
                Id = item.Id,
                Name = item.Name ?? string.Empty,
                BatchId = batch.BatchId,
                WorkId = item.WorkId,
                Status = item.Status,
                Message = item.Message,
                WorkJson = item.Work.Serialize(),
                DataJson = Helper.Serialize(item.Data) ?? string.Empty,
                DataType = item.Data.GetType().FullName,
                CreateUtcTime = now,
                UpdateUtcTime = now
            }).ToList();

            await AddTransaction(async (session) =>
            {
                await InsertOneAsync(_context.Batches, session, doc);

                foreach (var item in items)
                {
                    await InsertOneAsync(_context.BatchItems, session, item);
                }
            });
        }

        public async Task UpdateBatch(BaseBatchWork batchWork)
        {
            var update = Builders<BatchDocument>.Update
                .Set(t => t.Status, batchWork.Status)
                .Set(t => t.Message, batchWork.Message)
                .Set(t => t.RetryCount, batchWork.RetryCount)
                .Set(t => t.DealCount, batchWork.DealCount)
                .Set(t => t.ExpireUtcTime, batchWork.ExpireUtcTime);

            await AddTransaction((session) => UpdateOneAsync(_context.Batches, session,
                Builders<BatchDocument>.Filter.Eq(t => t.BatchId, batchWork.BatchId), update));
        }

        public async Task UpdateBatchItem(string batchId, string batchItemId, BatchStatus status, string message, string? workId = null)
        {
            var update = Builders<BatchItemDocument>.Update
                .Set(t => t.Status, status)
                .Set(t => t.Message, message);

            // null 表示不改动该列，空串表示清空（批量重试解绑失败子工作时用到）
            if (workId != null)
            {
                update = update.Set(t => t.WorkId, workId);
            }

            update = update.Set(t => t.UpdateUtcTime, Helper.CurrentUtcTime());

            var filter = Builders<BatchItemDocument>.Filter.And(
                Builders<BatchItemDocument>.Filter.Eq(t => t.BatchId, batchId),
                Builders<BatchItemDocument>.Filter.Eq(t => t.Id, batchItemId));

            await AddTransaction((session) => UpdateOneAsync(_context.BatchItems, session, filter, update));
        }

        #endregion

        #region Saga

        public async Task AddSaga(SagaInstance saga, IReadOnlyList<SagaStep> steps)
        {
            Helper.CheckArgumentNull(saga, nameof(saga));
            Helper.CheckArgumentNull(steps, nameof(steps));

            var now = Helper.CurrentUtcTime();

            var doc = new SagaDocument
            {
                SagaId = saga.SagaId,
                Name = saga.Name ?? string.Empty,
                CorrelationId = saga.CorrelationId ?? string.Empty,
                SagaType = saga.SagaType ?? string.Empty,
                InputJson = saga.InputJson ?? string.Empty,
                Status = saga.Status,
                StepCount = saga.StepCount,
                CurrentIndex = saga.CurrentIndex,
                CompensationIndex = saga.CompensationIndex,
                VariablesJson = Helper.Serialize(saga.Variables) ?? string.Empty,
                Message = saga.Message ?? string.Empty,
                ExpireUtcTime = saga.ExpireUtcTime,
                CreateUtcTime = saga.CreateUtcTime,
                UpdateUtcTime = now
            };

            var stepDocs = steps.Select(step => new SagaStepDocument
            {
                StepId = step.StepId,
                SagaId = saga.SagaId,
                Index = step.Index,
                Name = step.Name ?? string.Empty,
                ForwardMethod = step.ForwardMethod ?? string.Empty,
                CompensateMethod = step.CompensateMethod ?? string.Empty,
                OutputKey = step.OutputKey ?? string.Empty,
                Status = step.Status,
                Message = step.Message ?? string.Empty,
                ForwardWorkId = step.ForwardWorkId ?? string.Empty,
                CompensateWorkId = step.CompensateWorkId ?? string.Empty,
                CreateUtcTime = step.CreateUtcTime,
                UpdateUtcTime = now
            }).ToList();

            await AddTransaction(async (session) =>
            {
                await InsertOneAsync(_context.Sagas, session, doc);

                foreach (var step in stepDocs)
                {
                    await InsertOneAsync(_context.SagaSteps, session, step);
                }
            });
        }

        /// <summary>
        /// CAS 推进 saga 状态与两个游标：三者必须同时匹配，任一被别的推进者改过即 0 匹配 → 抛异常回滚。
        /// </summary>
        public async Task AdvanceSaga(SagaInstance saga, SagaStatus expectedStatus, int expectedCurrentIndex, int expectedCompensationIndex)
        {
            var filter = Builders<SagaDocument>.Filter.And(
                Builders<SagaDocument>.Filter.Eq(t => t.SagaId, saga.SagaId),
                Builders<SagaDocument>.Filter.Eq(t => t.Status, expectedStatus),
                Builders<SagaDocument>.Filter.Eq(t => t.CurrentIndex, expectedCurrentIndex),
                Builders<SagaDocument>.Filter.Eq(t => t.CompensationIndex, expectedCompensationIndex));

            var update = Builders<SagaDocument>.Update
                .Set(t => t.Status, saga.Status)
                .Set(t => t.CurrentIndex, saga.CurrentIndex)
                .Set(t => t.CompensationIndex, saga.CompensationIndex)
                .Set(t => t.Message, saga.Message ?? string.Empty)
                .Set(t => t.UpdateUtcTime, Helper.CurrentUtcTime());

            await AddTransaction(async (session) =>
            {
                var result = await UpdateOneAsync(_context.Sagas, session, filter, update);

                if (result.MatchedCount <= Helper.Zero)
                {
                    throw new SagaConcurrentUpdateException(
                        $"Saga {saga.SagaId} was already advanced by another processor; skip. (expected status {expectedStatus}, currentIndex {expectedCurrentIndex}, compensationIndex {expectedCompensationIndex})");
                }
            });
        }

        /// <summary>
        /// CAS 展开步骤：仅当步骤仍处于期望状态时才写入，保证同一步骤不会被展开两次。
        /// </summary>
        public async Task AdvanceSagaStep(string sagaId, string stepId, SagaStepStatus expectedStatus, SagaStepStatus newStatus,
            string? workId, bool isCompensate, string? message = null)
        {
            var filter = Builders<SagaStepDocument>.Filter.And(
                Builders<SagaStepDocument>.Filter.Eq(t => t.SagaId, sagaId),
                Builders<SagaStepDocument>.Filter.Eq(t => t.StepId, stepId),
                Builders<SagaStepDocument>.Filter.Eq(t => t.Status, expectedStatus));

            var update = Builders<SagaStepDocument>.Update.Set(t => t.Status, newStatus);

            // null 表示不改动该列（如"不可补偿 → Skipped"没有新工作 Id）
            if (workId != null)
            {
                update = isCompensate
                    ? update.Set(t => t.CompensateWorkId, workId)
                    : update.Set(t => t.ForwardWorkId, workId);
            }

            if (message != null)
            {
                update = update.Set(t => t.Message, message);
            }

            update = update.Set(t => t.UpdateUtcTime, Helper.CurrentUtcTime());

            await AddTransaction(async (session) =>
            {
                var result = await UpdateOneAsync(_context.SagaSteps, session, filter, update);

                if (result.MatchedCount <= Helper.Zero)
                {
                    throw new SagaConcurrentUpdateException(
                        $"Saga step {sagaId}:{stepId} was already expanded by another processor; skip. (expected status {expectedStatus})");
                }
            });
        }

        /// <summary>
        /// 无条件回写步骤状态（幂等目标状态）。这里刻意不用 CAS：
        /// CAS 冲突抛异常会把同一事务里的工作状态变更一起回滚，而工作确实已到终态、状态行必须落库。
        /// </summary>
        public async Task UpdateSagaStep(string sagaId, string stepId, SagaStepStatus status, string? message, string? workId, bool isCompensate)
        {
            var filter = Builders<SagaStepDocument>.Filter.And(
                Builders<SagaStepDocument>.Filter.Eq(t => t.SagaId, sagaId),
                Builders<SagaStepDocument>.Filter.Eq(t => t.StepId, stepId));

            var update = Builders<SagaStepDocument>.Update.Set(t => t.Status, status);

            if (message != null)
            {
                update = update.Set(t => t.Message, message);
            }

            if (workId != null)
            {
                update = isCompensate
                    ? update.Set(t => t.CompensateWorkId, workId)
                    : update.Set(t => t.ForwardWorkId, workId);
            }

            update = update.Set(t => t.UpdateUtcTime, Helper.CurrentUtcTime());

            await AddTransaction((session) => UpdateOneAsync(_context.SagaSteps, session, filter, update));
        }

        /// <summary>
        /// 列级更新变量池：只写 VariablesJson 一列。
        /// 变量池由步骤工作的回写事务写入，与 saga 推进事务并发，整行 Update 会让两者互相覆盖。
        /// </summary>
        public async Task UpdateSagaVariables(string sagaId, IDictionary<string, string> variables)
        {
            await AddTransaction((session) => UpdateOneAsync(_context.Sagas, session,
                Builders<SagaDocument>.Filter.Eq(t => t.SagaId, sagaId),
                Builders<SagaDocument>.Update.Set(t => t.VariablesJson, Helper.Serialize(variables) ?? string.Empty)));
        }

        public async Task RemoveExpiredSagas(DateTime now)
        {
            await AddTransaction(async (session) =>
            {
                // 仅清理"已过期且终态"的 saga，级联删除步骤行；
                // 未终态的 saga 即便过期也要留着继续推进 / 待人工续补
                var expired = await Find(_context.Sagas, session, Builders<SagaDocument>.Filter.And(
                        Builders<SagaDocument>.Filter.Lt(t => t.ExpireUtcTime, now),
                        Builders<SagaDocument>.Filter.In(t => t.Status,
                            new[] { SagaStatus.Completed, SagaStatus.Compensated, SagaStatus.Aborted })))
                    .Project(t => t.SagaId)
                    .ToListAsync();

                if (expired.Count == Helper.Zero)
                {
                    return;
                }

                await DeleteManyAsync(_context.SagaSteps, session, Builders<SagaStepDocument>.Filter.In(t => t.SagaId, expired));
                await DeleteManyAsync(_context.Sagas, session, Builders<SagaDocument>.Filter.In(t => t.SagaId, expired));
            });
        }

        #endregion

        #region 过期清理

        public async Task RemoveExpiredWorks(DateTime now)
        {
            await AddTransaction(async (session) =>
            {
                // 仅清理"已过期且终态"的工作，级联删除其状态行与队列消息。
                // 终态只取 succeeded/deleted，避免误删仍在流转（enqueued/processing/scheduled/awaiting）的任务。
                var expired = await Find(_context.Works, session, Builders<WorkDocument>.Filter.And(
                        Builders<WorkDocument>.Filter.Ne(t => t.ExpireUtcTime, null),
                        Builders<WorkDocument>.Filter.Lt(t => t.ExpireUtcTime, now),
                        Builders<WorkDocument>.Filter.In(t => t.StateName,
                            new[] { SucceededWorkState.Name, DeletedWorkState.Name })))
                    .Project(t => t.Id)
                    .ToListAsync();

                if (expired.Count == Helper.Zero)
                {
                    return;
                }

                await DeleteManyAsync(_context.States, session, Builders<StateDocument>.Filter.In(t => t.WorkId, expired));
                await DeleteManyAsync(_context.Queues, session, Builders<QueueDocument>.Filter.In(t => t.WorkId, expired));
                await DeleteManyAsync(_context.Works, session, Builders<WorkDocument>.Filter.In(t => t.Id, expired));
            });
        }

        public async Task RemoveExpiredBatches(DateTime now)
        {
            await AddTransaction(async (session) =>
            {
                var expired = await Find(_context.Batches, session, Builders<BatchDocument>.Filter.And(
                        Builders<BatchDocument>.Filter.Lt(t => t.ExpireUtcTime, now),
                        Builders<BatchDocument>.Filter.In(t => t.Status, new[] { BatchStatus.Success, BatchStatus.Failure })))
                    .Project(t => t.BatchId)
                    .ToListAsync();

                if (expired.Count == Helper.Zero)
                {
                    return;
                }

                await DeleteManyAsync(_context.BatchItems, session, Builders<BatchItemDocument>.Filter.In(t => t.BatchId, expired));
                await DeleteManyAsync(_context.Batches, session, Builders<BatchDocument>.Filter.In(t => t.BatchId, expired));
            });
        }

        /// <summary>
        /// 删除孤儿队列消息：工作行已不存在的队列行（如 RemoveWork 未清理队列、崩溃残留）。
        /// 用 $lookup 在库侧完成"工作行不存在"的判定，避免把全部 WorkId 拉回内存比对。
        /// </summary>
        public async Task RemoveOrphanQueueRows()
        {
            var workCollection = _context.CollectionName<WorkDocument>();

            var pipeline = new BsonDocument[]
            {
                new BsonDocument("$lookup", new BsonDocument
                {
                    { "from", workCollection },
                    { "localField", "WorkId" },
                    { "foreignField", "_id" },
                    { "as", "work" }
                }),
                new BsonDocument("$match", new BsonDocument("work.0", new BsonDocument("$exists", false))),
                new BsonDocument("$project", new BsonDocument("_id", 1))
            };

            var definition = new BsonDocumentStagePipelineDefinition<QueueDocument, QueueDocument>(pipeline);

            await AddTransaction(async (session) =>
            {
                var orphans = await (session == null
                        ? _context.Queues.Aggregate(definition)
                        : _context.Queues.Aggregate(session, definition))
                    .ToListAsync();

                if (orphans.Count == Helper.Zero)
                {
                    return;
                }

                var ids = orphans.Select(t => t.Id).ToList();

                await DeleteManyAsync(_context.Queues, session, Builders<QueueDocument>.Filter.In(t => t.Id, ids));
            });
        }

        #endregion

        #region 会话感知的原子操作（有无会话两条路径统一收口）

        private static Task InsertOneAsync<T>(IMongoCollection<T> collection, IClientSessionHandle? session, T document)
        {
            return session == null
                ? collection.InsertOneAsync(document)
                : collection.InsertOneAsync(session, document);
        }

        private static Task<UpdateResult> UpdateOneAsync<T>(IMongoCollection<T> collection, IClientSessionHandle? session,
            FilterDefinition<T> filter, UpdateDefinition<T> update)
        {
            return session == null
                ? collection.UpdateOneAsync(filter, update)
                : collection.UpdateOneAsync(session, filter, update);
        }

        private static Task<DeleteResult> DeleteManyAsync<T>(IMongoCollection<T> collection, IClientSessionHandle? session,
            FilterDefinition<T> filter)
        {
            return session == null
                ? collection.DeleteManyAsync(filter)
                : collection.DeleteManyAsync(session, filter);
        }

        /// <summary>
        /// 会话感知的查询。无会话（单机退化路径）时不能走 <c>Find(session, ...)</c> 扩展方法——
        /// 它要求 session 非空，传 null 会直接抛 ArgumentNullException。
        /// </summary>
        private static IFindFluent<T, T> Find<T>(IMongoCollection<T> collection, IClientSessionHandle? session,
            FilterDefinition<T> filter)
        {
            return session == null
                ? collection.Find(filter)
                : collection.Find(session, filter);
        }

        #endregion
    }
}
