using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Sagas;
using Gadamaxi.BackendWork.Sql.Tables;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Sql
{
    public class WorkStorage : IWorkStorage
    {
        private readonly List<Func<DBConnection, Task>> _cache;
        private readonly DataOptions _options;
        private readonly ILogger<WorkStorage> _logger;
        public WorkStorage(DataOptions options) 
        {
            Helper.CheckArgumentNull(options, nameof(options));
            _cache = new List<Func<DBConnection, Task>>();
            _options = options;
            _logger = DependencyInjectionHelper.Get<ILogger<WorkStorage>>();
        }

        private Task AddTransaction(Func<DBConnection, Task> action)
        {
            _cache.Add(action);

            return Task.CompletedTask;
        }

        public async Task AddCacheSet(string key, double score, string data)
        {
            await AddTransaction(async (db) =>
                await db.CacheSetTable
                    .Value(t => t.Type, key)
                    .Value(t => t.Score, score)
                    .Value(t => t.Value, data)
                    .InsertAsync()
            );
        }

        public async Task UpdateCacheSet(string key, double score, string data)
        {
            //_logger.LogInformation($"UpdateCacheSetDB {key} {data} {score}");
            await AddTransaction(async (db) =>
            await db.CacheSetTable
                .Where(t => t.Type == key && t.Value == data)
                .Set(t => t.Score, score)
                .UpdateAsync()
                );
        }

        public async Task AddRecurringWork(RecurringWork recurringwork)
        {
            await AddTransaction(async (db) =>
            await db.RecurringTable
                .Value(t => t.Id, recurringwork.Id)
                .Value(t => t.Name, recurringwork.Name)
                .Value(t => t.Cron, recurringwork.Cron)
                .Value(t => t.JsonContent, Helper.Serialize(recurringwork.WorkContent))
                .Value(t => t.Queue, recurringwork.Queue)
                .Value(t => t.TimeZone, recurringwork.TimeZone)
                .Value(t => t.CreateUtcTime, recurringwork.CreateUtcTime)
                .Value(t => t.NextExecutUtcTime, recurringwork.NextExecuteUtcTime)
                .Value(t => t.LastExecutUtcTime, recurringwork.LastExecuteUtcTime)
                .Value(t => t.LastWorkId, recurringwork.LastWorkId)
                .Value(t => t.RetryCount, recurringwork.RetryCount)
                .Value(t => t.ErrorInfo,recurringwork.ErrorInfo)
                .InsertAsync()
                );
                
        }

        public async Task AddServer(ServerInfo serverInfo)
        {
            await AddTransaction(async (db) =>
            {
                // 幂等注册：ServerName 通常来自固定配置，异常关闭后旧记录仍在表中，
                // 直接插入会因主键冲突导致宿主无法再次启动。先清理同名残留再插入。
                await db.ServerTable
                    .Where(t => t.ServerName == serverInfo.ServerName)
                    .DeleteAsync();

                await db.ServerTable.
                    Value(t => t.ServerName, serverInfo.ServerName)
                    .Value(t => t.CreateUtcTime, Helper.CurrentUtcTime())
                    .Value(t => t.LastHeartbeatUtcTime, serverInfo.HeartTime)
                    .Value(t => t.JsonData, Helper.Serialize(serverInfo.Data))
                    .InsertAsync();
            });
        }

        public async Task Commit()
        {
            using (var db = new DBConnection(_options))
            {
                using (var trans = db.BeginTransaction())
                {
                    foreach (var ca in _cache)
                    {
                        await ca(db);
                    }
                    await trans.CommitAsync();
                }
            }
            // 提交成功后清空队列：否则同一个 IWorkStorage 再次 Commit 会把已落库的写入重放一遍
            // （重复插入撞主键、重复 CAS 抛异常、重复计数翻倍）。
            _cache.Clear();
        }

        public async Task CreateWork(string id,Workload work, IDictionary<string, object>? addParameters, DateTime createUtcTime, TimeSpan expireIn, string name)
        {
            await AddTransaction(async (db) =>
            {
                await db.WorkTable
                    .Value(t => t.Id, id)
                    .Value(t => t.Name, name ?? string.Empty)
                    .Value(t => t.StateId, string.Empty)
                    .Value(t => t.StateName, string.Empty)
                    .Value(t => t.InvocationJson, work.Serialize())
                    .Value(t => t.ParametersJson, Helper.Serialize(addParameters) ?? string.Empty)
                    .Value(t => t.CreateUtcTime, createUtcTime)
                    .Value(t => t.ExpireUtcTime, createUtcTime.Add(expireIn))
                    .InsertAsync();
            });
        }

        public void Dispose()
        {
        }

        /// <summary>
        /// 入队消息与工作行、状态行在同一事务中提交：
        /// 消息绝不能先于工作行可见，否则消费端会读到"工作不存在"而把消息确认掉。
        /// </summary>
        public async Task Publish(QueueInfo info)
        {
            await AddTransaction(async (db) =>
                await db.QueueTable
                    .Value(t => t.WorkId, info.WorkId)
                    .Value(t => t.Queue, info.QueueName)
                    .InsertAsync()
            );
        }

        public async Task RemoveCacheSet(string name, string data)
        {
            await AddTransaction(async (db) =>
            await db.CacheSetTable
                .Where(t => t.Type == name && t.Value == data)
                .DeleteAsync());
        }

        public async Task RemoveRecurringWork(string id)
        {
            await AddTransaction(async (db) =>
            await db.RecurringTable
                .Where(t => t.Id == id)
                .DeleteAsync());
        }

        public async Task ServerHeartbeat(string serverName)
        {
            await AddTransaction(async (db) =>
            await db.ServerTable
                .Where(t => t.ServerName == serverName)
                .Set(t => t.LastHeartbeatUtcTime, Helper.CurrentUtcTime())
                .UpdateAsync());
        }

        public async Task SetWorkState(string workId, IWorkState initState)
        {
            await AddTransaction(async (db) =>
            {
                var stateId = Helper.NewId();

                await db.StateTable
                    .Value(t => t.Id, stateId)
                    .Value(t => t.Name, initState.StateName)
                    .Value(t => t.WorkId, workId)
                    .Value(t => t.Info, initState.Info)
                    .Value(t => t.CreateUtcTime, initState.CreateUtcTime)
                    .Value(t => t.JsonData, Helper.Serialize(initState.GetData()) ?? string.Empty)
                    .InsertAsync();

                await db.WorkTable
                    .Where(t => t.Id == workId)
                    .Set(t => t.StateId, stateId)
                    .Set(t => t.StateName, initState.StateName)
                    .UpdateAsync();
            });
        }

        public async Task UpdateRecurringWork(RecurringWork recurringwork)
        {
            await AddTransaction(async (db) =>
            await db.RecurringTable
                .Where(t => t.Id == recurringwork.Id)
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
                .Set(t => t.Paused, recurringwork.Paused)
                .UpdateAsync());
        }

        /// <summary>
        /// CAS 抢占周期任务的"本次触发"：WHERE LastExecutUtcTime = 期望版本，仅推进触发版本（与下次执行时间）。
        /// 只更新这两列，避免覆盖并发 AddOrUpdate 对 Name/Cron 等的改动。
        /// 抢占失败（0 行）时抛异常，中止并回滚整个触发事务，从而不写重复工作（#11）。
        /// </summary>
        public async Task AdvanceRecurringWork(RecurringWork recurringwork, DateTime? expectedLastExecuteUtcTime)
        {
            await AddTransaction(async (db) =>
            {
                var query = db.RecurringTable.Where(t => t.Id == recurringwork.Id);
                if (expectedLastExecuteUtcTime == null)
                {
                    // 首次触发：期望版本为 NULL，必须用 IS NULL 匹配（不能用参数化的 = NULL）
                    query = query.Where(t => t.LastExecutUtcTime == null);
                }
                else
                {
                    var expected = expectedLastExecuteUtcTime.Value;
                    query = query.Where(t => t.LastExecutUtcTime == expected);
                }

                var affected = await query
                    .Set(t => t.LastExecutUtcTime, recurringwork.LastExecuteUtcTime)
                    .Set(t => t.NextExecutUtcTime, recurringwork.NextExecuteUtcTime)
                    .UpdateAsync();

                if (affected <= 0)
                {
                    throw new RecurringDuplicateTriggerException(recurringwork.Id);
                }
            });
        }

        public async Task RemoveServer(DateTime expireTime)
        {
            await AddTransaction(async (db) =>
            await db.ServerTable
               .Where(t => t.LastHeartbeatUtcTime < expireTime)
               .DeleteAsync()
               );
        }

        public async Task RemoveExpiredWorks(DateTime now)
        {
            await AddTransaction(async (db) =>
            {
                // 仅清理"已过期且终态"的工作，级联删除其状态行与队列消息。
                // 终态只取 succeeded/deleted，避免误删仍在流转（enqueued/processing/scheduled/awaiting）的任务。
                var expired = db.WorkTable
                    .Where(w => w.ExpireUtcTime != null
                                && w.ExpireUtcTime < now
                                && (w.StateName == SucceededWorkState.Name || w.StateName == DeletedWorkState.Name));
                await db.StateTable.Where(st => expired.Any(w => w.Id == st.WorkId)).DeleteAsync();
                await db.QueueTable.Where(q => expired.Any(w => w.Id == q.WorkId)).DeleteAsync();
                await expired.DeleteAsync();
            });
        }

        public async Task RemoveExpiredBatches(DateTime now)
        {
            await AddTransaction(async (db) =>
            {
                // 已过期且终态（Success/Failure）的批量，级联删除批量项。
                var expired = db.BatchTable
                    .Where(b => b.ExpireUtcTime < now
                                && (b.Status == BatchStatus.Success || b.Status == BatchStatus.Failure));
                await db.BatchItemTable.Where(bi => expired.Any(b => b.BatchId == bi.BatchId)).DeleteAsync();
                await expired.DeleteAsync();
            });
        }

        public async Task RemoveOrphanQueueRows()
        {
            await AddTransaction(async (db) =>
            {
                // 孤儿队列消息：工作行已不存在的队列行。
                // 工作与队列在同一事务原子提交，未提交的中间态不会被误判为孤儿。
                await db.QueueTable
                    .Where(q => !db.WorkTable.Any(w => w.Id == q.WorkId))
                    .DeleteAsync();
            });
        }

        public async Task RemoveWork(string workId)
        {
            await AddTransaction(async (db) =>
            await db.WorkTable
                .Where(t=>t.Id == workId)
                .DeleteAsync()
                );
        }

        public async Task RemoveWorkState(string workId)
        {
            await AddTransaction(async (db) =>
            await db.StateTable
                .Where(t => t.WorkId == workId)
                .DeleteAsync()
                );
        }

        public async Task AddBatch(BatchWork batch)
        {
            await AddTransaction(async (db) =>
            {
                await db.BatchTable
                    .Value(t => t.BatchId, batch.BatchId)
                    .Value(t => t.Name, batch.Name ?? string.Empty)
                    .Value(t => t.Status, batch.Status)
                    .Value(t => t.RetryCount, batch.RetryCount)
                    .Value(t => t.Count, batch.Count)
                    .Value(t => t.DealCount, batch.DealCount)
                    .Value(t => t.Message, batch.Message)
                    .Value(t => t.ExpireUtcTime, batch.ExpireUtcTime)
                    .Value(t => t.ParentBatchId, batch.ParentBatchId)
                    .Value(t => t.CreateUtcTime, Helper.CurrentUtcTime())
                    .Value(t => t.UpdateUtcTime, Helper.CurrentUtcTime())
                    .InsertAsync();

                foreach (var item in batch.Items)
                {
                    var dataJson = Helper.Serialize(item.Data);
                    var dataType = item.Data.GetType().FullName;
                    var workJson = item.Work.Serialize();

                    await db.BatchItemTable
                        .Value(t => t.Id, item.Id)
                        .Value(t => t.Name, item.Name ?? string.Empty)
                        .Value(t => t.BatchId, batch.BatchId)
                        .Value(t => t.WorkId, item.WorkId)
                        .Value(t => t.Status, item.Status)
                        .Value(t => t.Message, item.Message)
                        .Value(t => t.WorkJson, workJson)
                        .Value(t => t.DataJson, dataJson)
                        .Value(t => t.DataType, dataType)
                        .Value(t => t.CreateUtcTime, Helper.CurrentUtcTime())
                        .Value(t => t.UpdateUtcTime, Helper.CurrentUtcTime())
                        .InsertAsync();
                }
            });
        }

        public async Task UpdateBatch(BaseBatchWork batchWork)
        {
            await AddTransaction(async (db) =>
            {
                await db.BatchTable
                    .Where(t => t.BatchId == batchWork.BatchId)
                    .Set(t => t.Status, batchWork.Status)
                    .Set(t => t.Message, batchWork.Message)
                    .Set(t => t.RetryCount, batchWork.RetryCount)
                    .Set(t => t.DealCount, batchWork.DealCount)
                    .Set(t => t.ExpireUtcTime, batchWork.ExpireUtcTime)
                    .UpdateAsync();
            });
        }

        public async Task UpdateBatchItem(string batchId, string batchItemId, BatchStatus status, string message, string? workId = null)
        {
            await AddTransaction(async (db) =>
            {
                var updater = db.BatchItemTable
                    .Where(t => t.BatchId == batchId && t.Id == batchItemId)
                    .Set(t => t.Status, status)
                    .Set(t => t.Message, message);

                // null 表示不改动该列，空串表示清空（批量重试解绑失败子工作时用到）
                if (workId != null)
                {
                    updater = updater.Set(t => t.WorkId, workId);
                }

                await updater
                    .Set(t => t.UpdateUtcTime, Helper.CurrentUtcTime())
                    .UpdateAsync();
            });
        }

        #region Saga

        public async Task AddSaga(SagaInstance saga, IReadOnlyList<SagaStep> steps)
        {
            Helper.CheckArgumentNull(saga, nameof(saga));
            Helper.CheckArgumentNull(steps, nameof(steps));

            await AddTransaction(async (db) =>
            {
                var now = Helper.CurrentUtcTime();

                await db.SagaTable
                    .Value(t => t.SagaId, saga.SagaId)
                    .Value(t => t.Name, saga.Name ?? string.Empty)
                    .Value(t => t.CorrelationId, saga.CorrelationId ?? string.Empty)
                    .Value(t => t.SagaType, saga.SagaType ?? string.Empty)
                    .Value(t => t.InputJson, saga.InputJson ?? string.Empty)
                    .Value(t => t.Status, saga.Status)
                    .Value(t => t.StepCount, saga.StepCount)
                    .Value(t => t.CurrentIndex, saga.CurrentIndex)
                    .Value(t => t.CompensationIndex, saga.CompensationIndex)
                    .Value(t => t.VariablesJson, Helper.Serialize(saga.Variables) ?? string.Empty)
                    .Value(t => t.Message, saga.Message ?? string.Empty)
                    .Value(t => t.ExpireUtcTime, saga.ExpireUtcTime)
                    .Value(t => t.CreateUtcTime, saga.CreateUtcTime)
                    .Value(t => t.UpdateUtcTime, now)
                    .InsertAsync();

                foreach (var step in steps)
                {
                    await db.SagaStepTable
                        .Value(t => t.StepId, step.StepId)
                        .Value(t => t.SagaId, saga.SagaId)
                        .Value(t => t.Index, step.Index)
                        .Value(t => t.Name, step.Name ?? string.Empty)
                        .Value(t => t.ForwardMethod, step.ForwardMethod ?? string.Empty)
                        .Value(t => t.CompensateMethod, step.CompensateMethod ?? string.Empty)
                        .Value(t => t.OutputKey, step.OutputKey ?? string.Empty)
                        .Value(t => t.Status, step.Status)
                        .Value(t => t.Message, step.Message ?? string.Empty)
                        .Value(t => t.ForwardWorkId, step.ForwardWorkId ?? string.Empty)
                        .Value(t => t.CompensateWorkId, step.CompensateWorkId ?? string.Empty)
                        .Value(t => t.CreateUtcTime, step.CreateUtcTime)
                        .Value(t => t.UpdateUtcTime, now)
                        .InsertAsync();
                }
            });
        }

        /// <summary>
        /// CAS 推进 saga 状态与两个游标。三者（状态、正向游标、补偿游标）必须同时匹配：
        /// 只要有一个已被别的推进者改过，便 0 行受影响 → 抛异常回滚整个推进事务。
        /// </summary>
        public async Task AdvanceSaga(SagaInstance saga, SagaStatus expectedStatus, int expectedCurrentIndex, int expectedCompensationIndex)
        {
            await AddTransaction(async (db) =>
            {
                var affected = await db.SagaTable
                    .Where(t => t.SagaId == saga.SagaId
                                && t.Status == expectedStatus
                                && t.CurrentIndex == expectedCurrentIndex
                                && t.CompensationIndex == expectedCompensationIndex)
                    .Set(t => t.Status, saga.Status)
                    .Set(t => t.CurrentIndex, saga.CurrentIndex)
                    .Set(t => t.CompensationIndex, saga.CompensationIndex)
                    .Set(t => t.Message, saga.Message ?? string.Empty)
                    .Set(t => t.UpdateUtcTime, Helper.CurrentUtcTime())
                    .UpdateAsync();

                if (affected <= Helper.Zero)
                {
                    throw new SagaConcurrentUpdateException(
                        $"Saga {saga.SagaId} was already advanced by another processor; skip. (expected status {expectedStatus}, currentIndex {expectedCurrentIndex}, compensationIndex {expectedCompensationIndex})");
                }
            });
        }

        /// <summary>
        /// CAS 展开步骤：仅当步骤仍处于期望状态时才写入，保证同一步骤不会被展开两次。
        /// 展开出的工作与本次步骤更新在同一事务提交，因此「工作已创建」与「步骤已推进」不会只成功一半。
        /// </summary>
        public async Task AdvanceSagaStep(string sagaId, string stepId, SagaStepStatus expectedStatus, SagaStepStatus newStatus,
            string? workId, bool isCompensate, string? message = null)
        {
            await AddTransaction(async (db) =>
            {
                var updater = db.SagaStepTable
                    .Where(t => t.SagaId == sagaId && t.StepId == stepId && t.Status == expectedStatus)
                    .Set(t => t.Status, newStatus);

                // null 表示不改动该列（如"不可补偿 → Skipped"没有新工作 Id）
                if (workId != null)
                {
                    updater = isCompensate
                        ? updater.Set(t => t.CompensateWorkId, workId)
                        : updater.Set(t => t.ForwardWorkId, workId);
                }

                if (message != null)
                {
                    updater = updater.Set(t => t.Message, message);
                }

                var affected = await updater
                    .Set(t => t.UpdateUtcTime, Helper.CurrentUtcTime())
                    .UpdateAsync();

                if (affected <= Helper.Zero)
                {
                    throw new SagaConcurrentUpdateException(
                        $"Saga step {sagaId}:{stepId} was already expanded by another processor; skip. (expected status {expectedStatus})");
                }
            });
        }

        /// <summary>
        /// 无条件回写步骤状态（幂等目标状态）。这里刻意不用 CAS，原因见接口注释：
        /// CAS 冲突抛异常会把同一事务里的工作状态变更一起回滚。
        /// </summary>
        public async Task UpdateSagaStep(string sagaId, string stepId, SagaStepStatus status, string? message, string? workId, bool isCompensate)
        {
            await AddTransaction(async (db) =>
            {
                var updater = db.SagaStepTable
                    .Where(t => t.SagaId == sagaId && t.StepId == stepId)
                    .Set(t => t.Status, status);

                if (message != null)
                {
                    updater = updater.Set(t => t.Message, message);
                }

                if (workId != null)
                {
                    updater = isCompensate
                        ? updater.Set(t => t.CompensateWorkId, workId)
                        : updater.Set(t => t.ForwardWorkId, workId);
                }

                await updater
                    .Set(t => t.UpdateUtcTime, Helper.CurrentUtcTime())
                    .UpdateAsync();
            });
        }

        /// <summary>
        /// 列级更新变量池：只更新 VariablesJson 一列。
        /// 变量池由步骤工作的回写事务写入，与 saga 推进事务并发，整行 Update 会互相覆盖。
        /// </summary>
        public async Task UpdateSagaVariables(string sagaId, IDictionary<string, string> variables)
        {
            await AddTransaction(async (db) =>
            await db.SagaTable
                .Where(t => t.SagaId == sagaId)
                .Set(t => t.VariablesJson, Helper.Serialize(variables) ?? string.Empty)
                .UpdateAsync());
        }

        public async Task RemoveExpiredSagas(DateTime now)
        {
            await AddTransaction(async (db) =>
            {
                // 仅清理"已过期且终态"的 saga，级联删除步骤行；未终态的 saga 即便过期也要留着继续推进/待人工续补
                var expired = db.SagaTable
                    .Where(s => s.ExpireUtcTime < now
                                && (s.Status == SagaStatus.Completed
                                    || s.Status == SagaStatus.Compensated
                                    || s.Status == SagaStatus.Aborted));
                await db.SagaStepTable.Where(st => expired.Any(s => s.SagaId == st.SagaId)).DeleteAsync();
                await expired.DeleteAsync();
            });
        }

        #endregion
    }
}
