using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Sagas;
using Gadamaxi.BackendWork.Storages;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using LinqToDB;
using LinqToDB.Data;
using System.Linq;
using LinqToDB.Common;
using Gadamaxi.BackendWork.Works.States;
using Gadamaxi.BackendWork.Batchs;
using LinqToDB.Async;

namespace Gadamaxi.BackendWork.Sql
{
    public class WorkQuery : IWorkQuery
    {
        private readonly DataOptions _options;

        /// <summary>
        /// 目标库是否 SqlServer。延迟探测：只有时间口径相关的查询才需要区分
        /// （SqlServer 的 datetime 列与 SQLite 的 TEXT 列可接受的文本精度不同）。
        /// </summary>
        private readonly Lazy<bool> _isSqlServer;

        public WorkQuery(DataOptions options)
        {
            _options = options;
            _isSqlServer = new Lazy<bool>(() =>
            {
                using (var db = new DBConnection(_options))
                {
                    return db.DataProvider != null
                        && db.DataProvider.Name.StartsWith(ProviderName.SqlServer, StringComparison.OrdinalIgnoreCase);
                }
            });
        }

        /// <summary>
        /// 把截止时刻格式化为可参与 SQL 侧比较的 UTC 文本：
        /// - SQLite 把 DateTime 落成 TEXT（带 6 位小数），必须用同格式才恒等可比；
        /// - SqlServer 的 <c>datetime</c> 列只接受 3 位小数（6 位直接报
        ///   "Conversion failed when converting date and/or time from character string"，
        ///   只有 <c>datetime2</c> 支持 6 位）。
        /// 因此按 provider 选择精度。
        /// </summary>
        private string FormatCutoff(DateTime utcTime)
        {
            var format = _isSqlServer.Value ? "yyyy-MM-dd HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss.ffffff";
            return utcTime.ToUniversalTime().ToString(format);
        }

        public async Task<bool> CheckCacheSet(string name, string value)
        {
            using (var db = new DBConnection(_options))
            {
                var q = from t in db.CacheSetTable
                        where t.Type == name && t.Value == value
                        select t.Id;
                var result = await q.CountAsync();

                return result > Helper.Zero;
            }
        }

        public async Task<bool> CheckWorkExist(string workId)
        {
            using (var db = new DBConnection(_options))
            {
                var q = from t in db.WorkTable
                        where t.Id == workId
                        select t.Id;
                var result = await q.CountAsync();
                return result > Helper.Zero;
            }
        }

        public async Task<int> CountBatchItems(string batchId, List<BatchStatus> statuses)
        {
            using (var db = new DBConnection(_options))
            {
                var q = from t in db.BatchItemTable
                        where t.BatchId == batchId && statuses.Contains(t.Status)
                        select t.Status;

                var rs = await q.CountAsync();

                return rs;
            }
        }

        public async Task<int> CountState(string wordId, string stateName)
        {
            using (var db = new DBConnection(_options))
            {
                var q = from t in db.StateTable
                        where t.WorkId == wordId && t.Name == stateName
                        select t.Name;

                return await q.CountAsync();
            }
        }

        public async Task<List<string>> GetStaleProcessingWorkIds(DateTime staleBeforeUtcTime, int batchSize)
        {
            // 截止时间统一格式化为 UTC 文本参与比较：
            // SQLite 将 DateTime 存为 TEXT（如 2026-09-17 15:48:30.588189Z），
            // 直接传 DateTime 参数会因映射格式差异导致文本比较失效；
            // MySQL / SqlServer 也能将该格式的字符串隐式转换为日期时间比较（精度按 provider 适配）。
            var cutoff = FormatCutoff(staleBeforeUtcTime);

            using (var db = new DBConnection(_options))
            {
                // 最后一条状态记录为 processing 且早于 cutoff 的工作（卡死任务）。
                // "最后一条"必须有确定性 tie-break：同一 work 的两条状态时间戳相同时，
                // 只按 CreateUtcTime 判会出现互为最后一条 → 同一 workId 重复返回、且可能
                // 把"处理中"误判成"已卡死"。以状态 Id 兜底（当前状态由 SetWorkState 生成，
                // Id 与写入顺序无关，但配合时间戳可保证唯一）。
                var q = (from s in db.StateTable
                         where s.Name == ProcessingWorkState.Name
                             && LinqToDB.Sql.Expr<bool>($"{s.CreateUtcTime} < {cutoff}")
                             && !db.StateTable.Any(l => l.WorkId == s.WorkId
                                 && (l.CreateUtcTime > s.CreateUtcTime
                                     || (l.CreateUtcTime == s.CreateUtcTime && string.Compare(l.Id, s.Id) > 0)))
                         select s.WorkId)
                         .Distinct();

                return await q.Take(batchSize).ToListAsync();
            }
        }

        public async Task<BaseBatchWork?> GetBatchById(string batchId)
        {
            using (var db = new DBConnection(_options))
            {
                var q = from t in db.BatchTable
                        where t.BatchId == batchId
                        select new BaseBatchWork
                        {
                            BatchId = t.BatchId,
                            Name = t.Name,
                            ParentBatchId = t.ParentBatchId,
                            Message = t.Message,
                            Status = t.Status,
                            RetryCount = t.RetryCount,
                            Count = t.Count,
                            DealCount = t.DealCount,
                            ExpireUtcTime = t.ExpireUtcTime
                        };

                return await q.FirstOrDefaultAsync();
            }
        }

        public async Task<BatchItem?> GetBatchItem(string batchId, string itemId)
        {
            using (var db = new DBConnection(_options))
            {
                var qi = from t in db.BatchItemTable
                         where t.BatchId == batchId && t.Id == itemId
                         select t.ToItem();

                return await qi.FirstOrDefaultAsync();
            }
        }

        public async Task<List<BatchItem>> GetBatchItems(List<BatchStatus> statuses, string batchId,int startIdx, int batchSize)
        {
            using (var db = new DBConnection(_options))
            {
                var qi = from t in db.BatchItemTable
                         where t.BatchId == batchId && statuses.Contains(t.Status)
                         select t.ToItem();
                var rs = await qi.Skip(startIdx).Take(batchSize).ToListAsync();

                return rs;
            }
        }

        public async Task<List<BatchItem>> GetBatchItems(string batchId, int startIdx, int batchSize)
        {
            using (var db = new DBConnection(_options))
            {
                var qi = from t in db.BatchItemTable
                         where t.BatchId == batchId
                         select t.ToItem();
                var rs = await qi.Skip(startIdx).Take(batchSize).ToListAsync();

                return rs;
            }
        }

        public async Task<List<string>> GetCacheSet(string name, long startTime, long endTime, int batchSize)
        {
            // Score 是 double 列，上下界必须以 double 参与比较：
            // 直接用 long（尤其 long.MaxValue）时，linq2db 会把值换算成 double 却仍按 BigInt 绑定参数，
            // SqlServer 上即抛 OverflowException（将参数值从 Double 转换到 Int64 失败）。
            // 换 (double) 后参数类型与列类型一致；SQLite 下两种写法等价。
            var lower = (double)startTime;
            var upper = (double)endTime;

            using (var db = new DBConnection(_options))
            {
                var q = from c in db.CacheSetTable
                        where c.Type == name && c.Score >= lower && c.Score < upper
                        orderby c.Score, c.Value
                        select c.Value;

                // 契约要求尊重 batchSize（Mongo 版已按 Limit 截断）：缺 Take 时整窗物化，
                // 到期项一多就是全表扫描 + 全量回传。
                return await q.Take(batchSize).ToListAsync();
            }
        }

        public async Task<BatchWork?> GetFullBatchById(string batchId)
        {
            using (var db = new DBConnection(_options))
            {
                var q = from t in db.BatchTable
                        where t.BatchId == batchId
                        select new BatchWork
                        {
                            BatchId = t.BatchId,
                            Name = t.Name,
                            ParentBatchId = t.ParentBatchId,
                            Message = t.Message,
                            Status = t.Status,
                            RetryCount = t.RetryCount,
                            ExpireUtcTime = t.ExpireUtcTime
                        };

                var qi = from t in db.BatchItemTable
                         where t.BatchId == batchId
                         select new BatchItem
                         {
                             Id = t.Id,
                             Name = t.Name,
                             Status = t.Status,
                             Message = t.Message,
                             Work = Workload.Deserialize(t.WorkJson),
                             WorkId = t.WorkId,
                             Data = (BatchItemData)Helper.Deserialize(t.DataJson, Helper.GetType(t.DataType))
                         };

                var data = await q.FirstOrDefaultAsync();
                if (data != null)
                {
                    data.Items = await qi.ToListAsync();
                }

                return data;
            }
        }

        public async Task<IWorkState?> GetLastState(string wordId)
        {
            using (var db = new DBConnection(_options))
            {
                var q = from t in db.StateTable
                        where t.WorkId == wordId
                        orderby t.CreateUtcTime descending
                        select t;
                var data = await q.FirstOrDefaultAsync();

                if (data == null)
                {
                    return null;
                }

                return data.ToState();
            }
        }

        public async Task<RecurringWork?> GetRecurringWorkById(string id)
        {
            using (var db = new DBConnection(_options))
            {
                var q = from r in db.RecurringTable
                        where r.Id == id
                        select new RecurringWork(id)
                        {
                            Name = r.Name,
                            Cron = r.Cron,
                            WorkContent = r.JsonContent,
                            Queue = r.Queue,
                            TimeZone = r.TimeZone,
                            CreateUtcTime = r.CreateUtcTime,
                            NextExecuteUtcTime = r.NextExecutUtcTime,
                            LastExecuteUtcTime = r.LastExecutUtcTime,
                            LastWorkId = r.LastWorkId,
                            RetryCount = r.RetryCount,
                            ErrorInfo = r.ErrorInfo,
                            Paused = r.Paused
                        };

                return await q.FirstOrDefaultAsync();
            }
        }

        public async Task<WorkData?> GetWorkData(string workId)
        {
            using (var db = new DBConnection(_options))
            {
                var q = from r in db.WorkTable
                        where r.Id == workId
                        select new WorkData
                        {
                            WorkId = workId,
                            Name = r.Name,
                            Content = Helper.Deserialize<WorkloadContent>(r.InvocationJson),
                            StateName = r.StateName,
                            Parameters = Helper.Deserialize<Dictionary<string, string>>(r.ParametersJson),
                            CreatedUctTime = r.CreateUtcTime,
                            ExpireUtcTime = r.ExpireUtcTime
                        };

                return await q.FirstOrDefaultAsync();
            }
                    
        }

        #region Saga

        public async Task<SagaInstance?> GetSagaById(string sagaId)
        {
            using (var db = new DBConnection(_options))
            {
                var q = from t in db.SagaTable
                        where t.SagaId == sagaId
                        select t.ToSaga();

                return await q.FirstOrDefaultAsync();
            }
        }

        public async Task<SagaInstance?> GetSagaByCorrelationId(string correlationId)
        {
            using (var db = new DBConnection(_options))
            {
                var q = from t in db.SagaTable
                        where t.CorrelationId == correlationId
                        select t.ToSaga();

                return await q.FirstOrDefaultAsync();
            }
        }

        /// <summary>
        /// 步骤必须按序号升序返回：游标（CurrentIndex / CompensationIndex）是按序号定位步骤的，
        /// 顺序错乱会直接导致推进到错误的步骤。这里显式 ORDER BY，不依赖存储的自然行序。
        /// </summary>
        public async Task<List<SagaStep>> GetSagaSteps(string sagaId)
        {
            using (var db = new DBConnection(_options))
            {
                var q = from t in db.SagaStepTable
                        where t.SagaId == sagaId
                        orderby t.Index ascending
                        select t.ToStep();

                return await q.ToListAsync();
            }
        }

        public async Task<List<string>> GetTimeoutSagaIds(DateTime startedBeforeUtcTime, int batchSize)
        {
            // 与 GetStaleProcessingWorkIds 同一套写法：截止时间格式化为 UTC 文本参与比较。
            // SQLite 将 DateTime 存为 TEXT，直接传 DateTime 参数会因映射格式差异导致文本比较失效；
            // 时间比较放 SQL 侧还避免了"SQLite 读回的是本地时区帧"这一跨库陷阱。
            var cutoff = FormatCutoff(startedBeforeUtcTime);

            using (var db = new DBConnection(_options))
            {
                var q = from s in db.SagaTable
                        where s.Status == SagaStatus.Running
                            && LinqToDB.Sql.Expr<bool>($"{s.CreateUtcTime} < {cutoff}")
                        select s.SagaId;

                return await q.Take(batchSize).ToListAsync();
            }
        }

        #endregion
    }
}
