using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// saga 的创建与管理入口实现。
    /// <para>
    /// 这里只做「登记 / 查询 / 人工干预」：真正的编排推进在 <see cref="SagaWorkProcess"/> 里，
    /// 由它按调度缓存周期推进游标。人工干预（取消 / 续补）需要读后写（先读游标与步骤状态，再改），
    /// 因此与推进进程共用同一把 saga 分布式锁串行化，CAS 是锁过期/时钟漂移时的第二道防线。
    /// </para>
    /// </summary>
    public class SagaManager : ISagaManager
    {
        private readonly IWorkQuery _workQuery;
        private readonly IDistributedLock _distributedLock;
        private readonly WorkServerOption _option;
        private readonly ILogger<SagaManager> _logger;

        public SagaManager(IWorkQuery workQuery,
            IDistributedLock distributedLock,
            WorkServerOption option,
            ILogger<SagaManager> logger)
        {
            _workQuery = workQuery;
            _distributedLock = distributedLock;
            _option = option;
            _logger = logger;
        }

        public async Task<string> Start<TSaga>(TSaga saga) where TSaga : Saga<TSaga>
        {
            Helper.CheckArgumentNull(saga, nameof(saga));
            // saga 名上限比普通任务名小：补偿工作名还要追加 "~undo" 后缀
            Helper.CheckWorkName(saga.Name, Helper.MaxSagaNameLength);
            CheckCorrelationId(saga.CorrelationId);

            // 幂等创建：同一业务关联键已经启动过就直接返回既有 saga，不重复创建
            var exists = await _workQuery.GetSagaByCorrelationId(saga.CorrelationId);
            if (exists != null)
            {
                _logger.LogInformation($"Saga with correlation '{saga.CorrelationId}' already exists: {exists.SagaId}.");
                return exists.SagaId;
            }

            var ctx = new SagaContext();
            ctx.Saga.Name = saga.Name;
            ctx.Saga.CorrelationId = saga.CorrelationId;
            // 流程定义来自实现类：步骤与补偿都声明在 TSaga 里（D1），这里只负责收集
            saga.Build(new SagaDefinition<TSaga>(ctx));

            if (ctx.Steps.Count <= Helper.Zero)
            {
                throw new BackendException($"Saga '{saga.Name}' has no step, nothing to orchestrate.");
            }

            // 启动期校验（D6）：实例可创建、参数可绑定、名称与 outputKey 合法、输入可序列化
            SagaStartValidator.Validate(saga, ctx);

            // 实例还原（D2=B）的两块数据：实现类定位 + 输入快照
            ctx.Saga.SagaType = typeof(TSaga).AssemblyQualifiedName!;
            ctx.Saga.InputJson = Helper.Serialize(saga) ?? string.Empty;
            ctx.Saga.StepCount = ctx.Steps.Count;
            ctx.Saga.CurrentIndex = Helper.One;
            ctx.Saga.CompensationIndex = Helper.Zero;

            var now = Helper.CurrentUtcTime();
            ctx.Saga.CreateUtcTime = now;
            ctx.Saga.UpdateUtcTime = now;
            ctx.Saga.ExpireUtcTime = now.Add(_option.SagaExpireTime);

            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                try
                {
                    await storage.AddSaga(ctx.Saga, ctx.Steps);

                    // 登记调度缓存必须与 saga 行同事务：否则可能出现"saga 已存在但永远不会被调度"
                    await storage.AddCacheSet(Helper.SagaKey, now.UnixMillisecondStamp(), ctx.Saga.SagaId);

                    await storage.Commit();
                }
                catch (Exception ex)
                {
                    // 唯一索引兜底：并发启动同一业务键时，后到的那次会撞唯一约束。
                    // 这不是错误，重新读一次拿既有 saga 返回即可（与前置查重形成双保险）。
                    var race = await _workQuery.GetSagaByCorrelationId(saga.CorrelationId);
                    if (race != null)
                    {
                        _logger.LogInformation($"Saga with correlation '{saga.CorrelationId}' was created concurrently: {race.SagaId}.");
                        return race.SagaId;
                    }

                    _logger.LogWarning($"Create saga '{saga.Name}' failure.{ex.Message}\n{ex.GetStacks()}");
                    throw;
                }
            }

            _logger.LogInformation($"Created saga {ctx.Saga.SagaId} '{saga.Name}' with {ctx.Steps.Count} step(s).");
            return ctx.Saga.SagaId;
        }

        public async Task<bool> Cancel(string sagaId, string reason = "")
        {
            Helper.CheckArgumentNull(sagaId, nameof(sagaId));

            using (await AcquireSagaLock(sagaId))
            {
                var saga = await _workQuery.GetSagaById(sagaId);
                if (saga == null)
                {
                    throw new BackendException($"Saga {sagaId} not exists.");
                }

                // 已在补偿中或已到终态：取消是幂等的空操作
                if (saga.Status == SagaStatus.Compensating || saga.Finish())
                {
                    _logger.LogInformation($"Saga {sagaId} status is {saga.Status}, cancel is ignored.");
                    return false;
                }

                using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
                {
                    var message = string.IsNullOrEmpty(reason) ? "canceled." : $"canceled: {reason}";

                    // 走与"正向失败"完全相同的回滚路径：CompensationIndex = CurrentIndex - 1
                    var canceled = await SagaFactory.Wake(storage, _logger, saga,
                        SagaStatus.Compensating,
                        saga.CurrentIndex,
                        saga.CurrentIndex - Helper.One,
                        message);

                    CheckAdvanced(sagaId, canceled, nameof(Cancel));
                }

                _logger.LogInformation($"Saga {sagaId} canceled from step {saga.CurrentIndex}.");
                return true;
            }
        }

        public async Task RetryCompensation(string sagaId)
        {
            Helper.CheckArgumentNull(sagaId, nameof(sagaId));

            using (await AcquireSagaLock(sagaId))
            {
                var saga = await _workQuery.GetSagaById(sagaId);
                if (saga == null)
                {
                    throw new BackendException($"Saga {sagaId} not exists.");
                }

                if (saga.Status != SagaStatus.Aborted)
                {
                    throw new BackendException($"Saga {sagaId} status {saga.Status} can not retry compensation.");
                }

                var steps = await _workQuery.GetSagaSteps(sagaId);
                var step = steps.FirstOrDefault(t => t.Index == saga.CompensationIndex);

                using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
                {
                    // 补偿游标保持不变：只重试失败的那一步。
                    // 该步骤当前是 CompensationFailed，补偿路径不会处理它，
                    // 因此要退回 Succeeded —— 与"正向成功过、必须补偿"的展开条件对齐。
                    if (step != null && step.Status == SagaStepStatus.CompensationFailed)
                    {
                        await storage.UpdateSagaStep(sagaId, step.StepId, SagaStepStatus.Succeeded, null, null, true);
                    }

                    var retried = await SagaFactory.Wake(storage, _logger, saga,
                        SagaStatus.Compensating,
                        saga.CurrentIndex,
                        saga.CompensationIndex,
                        $"retry compensation from step {saga.CompensationIndex}.");

                    CheckAdvanced(sagaId, retried, nameof(RetryCompensation));
                }

                _logger.LogInformation($"Saga {sagaId} compensation retried from step {saga.CompensationIndex}.");
            }
        }

        public Task<SagaInstance?> GetSaga(string sagaId)
        {
            Helper.CheckArgumentNull(sagaId, nameof(sagaId));
            return _workQuery.GetSagaById(sagaId);
        }

        public Task<SagaInstance?> GetSagaByCorrelationId(string correlationId)
        {
            Helper.CheckArgumentNull(correlationId, nameof(correlationId));
            return _workQuery.GetSagaByCorrelationId(correlationId);
        }

        public Task<List<SagaStep>> GetSteps(string sagaId)
        {
            Helper.CheckArgumentNull(sagaId, nameof(sagaId));
            return _workQuery.GetSagaSteps(sagaId);
        }

        private static void CheckCorrelationId(string correlationId)
        {
            Helper.CheckArgumentNull(correlationId, $"{nameof(ISaga)}.{nameof(ISaga.CorrelationId)}");

            if (correlationId.Length > Helper.MaxWorkNameLength)
            {
                throw new ArgumentException(
                    $"{nameof(ISaga.CorrelationId)} length {correlationId.Length} exceeds {Helper.MaxWorkNameLength}.",
                    nameof(correlationId));
            }
        }

        private async Task<IDisposable> AcquireSagaLock(string sagaId)
        {
            try
            {
                return await _distributedLock.AcquireLock(sagaId, Helper.DistributedLockTimeOut);
            }
            catch (LockBackendException ex)
            {
                // 取不到锁 = 该 saga 正在被调度推进。人工干预读后写，不能与推进交错，交给调用方稍后重试
                throw new BackendException($"Saga {sagaId} is being advanced by another processor, retry later. {ex.Message}", ex);
            }
        }

        /// <summary>
        /// CAS 冲突说明在拿到锁之后、提交之前被别的推进者抢先（锁过期 / 时钟漂移），
        /// 本次干预没有生效。返回 false 会被误读成"无事可做"，因此这里显式抛错让调用方重试。
        /// </summary>
        private static void CheckAdvanced(string sagaId, bool advanced, string operation)
        {
            if (!advanced)
            {
                throw new BackendException($"Saga {sagaId} {operation} was preempted by a concurrent advance, retry later.");
            }
        }
    }
}
