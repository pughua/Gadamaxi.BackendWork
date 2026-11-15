using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Resources;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// saga 推进进程：按调度缓存周期扫描待推进的 saga，并"一步一轮"地推进游标。
    /// <para>
    /// <b>严格串行 + 单写者</b>：正向只展开 <c>CurrentIndex</c> 指向的那一步，补偿只展开
    /// <c>CompensationIndex</c> 指向的那一步。因此不需要 <c>AwaitingWorkState</c> 串联，
    /// 也不依赖存储行序 —— 这是 Saga 与 Batch（并行展开）的本质区别。
    /// 同一 saga 的写入只发生在持有 <c>IDistributedLock(sagaId)</c> 的进程里，CAS 是第二道防线。
    /// </para>
    /// 进程本身是普通 <see cref="IWorkProcess"/>，轮询全部带停止令牌，停机可立即退出（无定时器）。
    /// </summary>
    internal class SagaWorkProcess : IWorkProcess
    {
        /// <summary>不可补偿步骤在回滚时的留痕消息</summary>
        internal const string NoCompensateMessage = "no compensate action declared (StepNoCompensate); skipped.";

        private readonly IDistributedLock _distributedLock;
        private readonly IWorkQuery _workQuery;
        private readonly IWorkQueue _workQueue;
        private readonly IWorkStateHandlerProvider _provider;
        private readonly ILogger<SagaWorkProcess> _logger;
        private readonly WorkServerOption _option;

        public SagaWorkProcess(IDistributedLock distributedLock,
            IWorkQuery workQuery,
            IWorkQueue workQueue,
            IWorkStateHandlerProvider provider,
            ILogger<SagaWorkProcess> logger,
            WorkServerOption option)
        {
            _distributedLock = distributedLock;
            _workQuery = workQuery;
            _workQueue = workQueue;
            _provider = provider;
            _logger = logger;
            _option = option;
        }

        public async Task Execute(WorkProcessContext context)
        {
            var count = Helper.Zero;

            do
            {
                count = Helper.Zero;
                var ids = await WorkFactory.GetCacheDatas(_workQuery, _distributedLock, Helper.SagaKey, _option.SagaPollingDelay, _logger);

                foreach (var id in ids)
                {
                    try
                    {
                        await Advance(id);
                        count++;
                    }
                    catch (LockBackendException ex)
                    {
                        _logger.LogTrace(ex.Message);
                    }
                    catch (SagaConcurrentUpdateException ex)
                    {
                        // CAS 抢占失败 = 该次推进已被别的推进者处理，属正常并发去重而非故障
                        _logger.LogInformation(ex.Message);
                    }
                }
            } while (count > Helper.Zero);

            if (count <= Helper.Zero)
            {
                await Helper.Delay(_option.SagaPollingDelay, Helper.DefaultSagaPollingDelay, count, context.Server.StopToken);
            }
        }

        /// <summary>
        /// 推进一个 saga 一轮。进程崩溃后任意节点都能接管：全部结论都从库中读，
        /// 不依赖任何进程内状态。
        /// </summary>
        internal async Task Advance(string sagaId)
        {
            using (await _distributedLock.AcquireLock(sagaId, Helper.DistributedLockTimeOut))
            {
                var saga = await _workQuery.GetSagaById(sagaId);
                if (saga == null)
                {
                    // saga 已被清理：把它从调度缓存摘掉，否则会被一直扫描
                    await RemoveFromCache(sagaId);
                    _logger.LogTrace($"Saga {sagaId} removed.");
                    return;
                }

                if (saga.Finish())
                {
                    // 终态与移出缓存是同一事务，这里只是兜底清理遗留的缓存行
                    await RemoveFromCache(sagaId);
                    _logger.LogTrace($"Saga {sagaId} is {saga.Status}. No need to advance.");
                    return;
                }

                var steps = await _workQuery.GetSagaSteps(sagaId);

                switch (saga.Status)
                {
                    case SagaStatus.None:
                        // 首次调度：登记阶段。CAS 保证两个节点同时扫描时只会有一次 None -> Running
                        await AdvanceSaga(saga, SagaStatus.Running, saga.CurrentIndex, saga.CompensationIndex, string.Empty);
                        break;
                    case SagaStatus.Running:
                        await AdvanceForward(saga, steps);
                        break;
                    case SagaStatus.Compensating:
                        await AdvanceCompensation(saga, steps);
                        break;
                    default:
                        _logger.LogWarning($"Saga {sagaId} status {saga.Status} can not be advanced.");
                        break;
                }
            }
        }

        #region 正向推进

        private async Task AdvanceForward(SagaInstance saga, List<SagaStep> steps)
        {
            var step = FindStep(steps, saga.CurrentIndex);
            if (step == null)
            {
                // 游标越过最后一步 = 正向全部完成
                using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
                {
                    var completed = await SagaFactory.Finish(storage, _logger, saga, SagaStatus.Completed,
                        $"{nameof(SagaStatus.Completed)},all {saga.StepCount} step(s) succeeded.");
                    if (completed)
                    {
                        _logger.LogInformation($"Saga {saga.SagaId} completed.");
                    }
                }
                return;
            }

            switch (step.Status)
            {
                case SagaStepStatus.Init:
                    await ExpandForward(saga, step);
                    break;

                case SagaStepStatus.Running:
                    // 步骤正在执行：等状态回写管线唤醒，或等下一轮扫描
                    break;

                case SagaStepStatus.Succeeded:
                    await AdvanceSaga(saga, SagaStatus.Running, saga.CurrentIndex + Helper.One,
                        saga.CompensationIndex, string.Empty);
                    break;

                case SagaStepStatus.Failed:
                    // 正向终态失败（重试已耗尽）→ 转入逆序补偿
                    await BeginCompensation(saga, steps, step);
                    break;

                default:
                    // 游标指向了不该出现在正向路径上的状态：本轮不推进，避免把状态带偏
                    _logger.LogWarning($"Saga {saga.SagaId} step {step.Index} status is {step.Status} while running forward; skip this round.");
                    break;
            }
        }

        private async Task ExpandForward(SagaInstance saga, SagaStep step)
        {
            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                string? workId = null;

                var expanded = await WorkFactory.CommitWithCasGuard(storage, _logger, async () =>
                {
                    // 步骤工作负载是框架入口 SagaStepRunner.Run：执行期由它还原 saga 实例
                    // 再按方法名调用业务方法（D2=B 实例还原）。工作名仍是步骤方法名（D4）。
                    var load = StepRunnerWorkload(saga.SagaId, step.StepId, false);

                    workId = await WorkFactory.CreateEnqueueWork(storage, _workQueue, _provider, load,
                        Helper.CurrentUtcTime(),
                        string.Format(Resource.TriggerBy, nameof(SagaWorkProcess)),
                        BuildParameters(saga.SagaId, step, false),
                        step.Name);

                    // CAS（期望 Init → Running）与工作创建在同一延迟事务：
                    // 两者要么一起落库，要么一起回滚，不会出现"工作建了但步骤没推进"
                    await storage.AdvanceSagaStep(saga.SagaId, step.StepId,
                        SagaStepStatus.Init, SagaStepStatus.Running, workId, false);
                }, $"Saga {saga.SagaId} step {step.Index} was already expanded.");

                if (expanded)
                {
                    _logger.LogInformation($"Saga {saga.SagaId} expanded forward step {step.Index} to work {workId}.");
                }
            }
        }

        /// <summary>
        /// 正向失败：留痕后续步骤不会再执行，并把 saga 转入补偿。
        /// <para>
        /// 留痕写入先排进同一存储的延迟事务，再交给 <see cref="SagaFactory.Advance"/> 一并提交 ——
        /// 若推进的 CAS 被抢先，整个事务回滚，留痕也一起撤销，不会留下"步骤已标 Skipped 但流程还在正向跑"的错位状态。
        /// </para>
        /// </summary>
        private async Task BeginCompensation(SagaInstance saga, List<SagaStep> steps, SagaStep failedStep)
        {
            var currentIndex = saga.CurrentIndex;

            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                foreach (var step in steps)
                {
                    if (step.Index > currentIndex && step.Status == SagaStepStatus.Init)
                    {
                        await storage.UpdateSagaStep(saga.SagaId, step.StepId, SagaStepStatus.Skipped,
                            $"step {failedStep.Index} failed; this step will not be executed.", null, false);
                    }
                }

                // 补偿从失败步骤的前一步开始：失败步骤本身不补偿（正向步骤失败即视为未生效）
                var started = await SagaFactory.Advance(storage, _logger, saga,
                    SagaStatus.Compensating, currentIndex, currentIndex - Helper.One, failedStep.Message);

                if (started)
                {
                    _logger.LogWarning($"Saga {saga.SagaId} step {failedStep.Index} failed, starting compensation from step {saga.CompensationIndex}. {failedStep.Message}");
                }
            }
        }

        #endregion

        #region 补偿推进

        private async Task AdvanceCompensation(SagaInstance saga, List<SagaStep> steps)
        {
            if (saga.CompensationIndex < Helper.One)
            {
                using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
                {
                    var compensated = await SagaFactory.Finish(storage, _logger, saga, SagaStatus.Compensated,
                        $"{nameof(SagaStatus.Compensated)},all succeeded step(s) compensated.");
                    if (compensated)
                    {
                        _logger.LogInformation($"Saga {saga.SagaId} compensated.");
                    }
                }
                return;
            }

            var step = FindStep(steps, saga.CompensationIndex);
            if (step == null)
            {
                _logger.LogWarning($"Saga {saga.SagaId} step {saga.CompensationIndex} not found while compensating; skip this round.");
                return;
            }

            switch (step.Status)
            {
                case SagaStepStatus.Succeeded:
                    // 正向成功过 → 必须补偿
                    await ExpandCompensation(saga, step);
                    break;

                case SagaStepStatus.Compensating:
                    // 补偿正在执行
                    break;

                case SagaStepStatus.Compensated:
                    await MoveCompensationBack(saga);
                    break;

                case SagaStepStatus.CompensationFailed:
                    // 补偿终态失败：不再自动重试（无限重试会掩盖问题），停下来等人工续补
                    await AbortSaga(saga, step);
                    break;

                default:
                    // Init / Failed / Skipped：该步骤没有产生需要撤销的副作用，直接越过
                    await MoveCompensationBack(saga);
                    break;
            }
        }

        private async Task ExpandCompensation(SagaInstance saga, SagaStep step)
        {
            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                string? workId = null;

                var expanded = await WorkFactory.CommitWithCasGuard(storage, _logger, async () =>
                {
                    if (!step.HasCompensate())
                    {
                        // 不可补偿步骤（创建时由 StepNoCompensate 显式标注）：不展开任何工作，
                        // 记为 Skipped 并留痕，说明"这里为什么没有撤销动作"
                        await storage.AdvanceSagaStep(saga.SagaId, step.StepId,
                            SagaStepStatus.Succeeded, SagaStepStatus.Skipped, null, false, NoCompensateMessage);
                        return;
                    }

                    // 工作负载同为 SagaStepRunner.Run（isCompensate=true），工作名加 "~undo" 后缀
                    var load = StepRunnerWorkload(saga.SagaId, step.StepId, true);

                    workId = await WorkFactory.CreateEnqueueWork(storage, _workQueue, _provider, load,
                        Helper.CurrentUtcTime(),
                        string.Format(Resource.TriggerBy, nameof(SagaWorkProcess)),
                        BuildParameters(saga.SagaId, step, true),
                        step.CompensateName());

                    await storage.AdvanceSagaStep(saga.SagaId, step.StepId,
                        SagaStepStatus.Succeeded, SagaStepStatus.Compensating, workId, true);
                }, $"Saga {saga.SagaId} step {step.Index} compensate was already expanded.");

                if (expanded)
                {
                    _logger.LogInformation(workId == null
                        ? $"Saga {saga.SagaId} step {step.Index} has no compensate action, marked skipped."
                        : $"Saga {saga.SagaId} expanded compensate step {step.Index} to work {workId}.");
                }
            }
        }

        private async Task MoveCompensationBack(SagaInstance saga)
        {
            await AdvanceSaga(saga, SagaStatus.Compensating, saga.CurrentIndex,
                saga.CompensationIndex - Helper.One, string.Empty);
        }

        private async Task AbortSaga(SagaInstance saga, SagaStep step)
        {
            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                var aborted = await SagaFactory.Finish(storage, _logger, saga, SagaStatus.Aborted,
                    $"compensate step {step.Index} failed: {step.Message}");

                if (aborted)
                {
                    _logger.LogWarning($"Saga {saga.SagaId} aborted, manual intervention required. {step.Message}");
                }
            }
        }

        #endregion

        #region helpers

        /// <summary>
        /// 步骤按序号定位（不依赖列表下标）：序号就是游标语义，越界即代表正向/补偿走完。
        /// </summary>
        private static SagaStep? FindStep(List<SagaStep> steps, int index)
        {
            return steps.FirstOrDefault(t => t.Index == index);
        }

        /// <summary>
        /// 步骤工作的参数：让回写管线认出这是 saga 的步骤、以及该回写哪个方向。
        /// 与批量用 <c>BatchId</c>/<c>BatchItemId</c> 标记子工作完全对称。
        /// </summary>
        private static Dictionary<string, object> BuildParameters(string sagaId, SagaStep step, bool isCompensate)
        {
            var parameters = new Dictionary<string, object>
            {
                { Helper.SagaIdKey, sagaId },
                { Helper.SagaStepIdKey, step.StepId }
            };

            if (isCompensate)
            {
                parameters.Add(Helper.SagaCompensateKey, Helper.One);
            }
            else if (!string.IsNullOrEmpty(step.OutputKey))
            {
                parameters.Add(Helper.SagaOutputKeyKey, step.OutputKey);
            }

            return parameters;
        }

        /// <summary>
        /// 步骤工作负载：<c>SagaStepRunner.Run(sagaId, stepId, isCompensate)</c>。
        /// 参数值在声明此刻求值固化（sagaId / stepId 是不可变 Id，因此安全）；
        /// 不能用 <c>Expression&lt;Func&lt;_, Task&gt;&gt;</c> 的公开重载 —— <c>Run</c> 返回
        /// <c>Task&lt;object?&gt;</c>，转换成 Task 会让表达式树顶层变成 Convert 节点，
        /// 所以直接走接收 LambdaExpression 的内部重载。
        /// </summary>
        private static Workload StepRunnerWorkload(string sagaId, string stepId, bool isCompensate)
        {
            return Workload.FromExpression(
                (Expression<Func<SagaStepRunner, Task<object>>>)(r => r.Run(sagaId, stepId, isCompensate)),
                typeof(SagaStepRunner), string.Empty);
        }

        /// <summary>推进游标 / 阶段（不改动调度缓存：扫描器每轮会把分数推后，没必要额外写）</summary>
        private async Task<bool> AdvanceSaga(SagaInstance saga, SagaStatus newStatus,
            int newCurrentIndex, int newCompensationIndex, string message)
        {
            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                return await SagaFactory.Advance(storage, _logger, saga, newStatus,
                    newCurrentIndex, newCompensationIndex, message);
            }
        }

        private async Task RemoveFromCache(string sagaId)
        {
            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                await storage.RemoveCacheSet(Helper.SagaKey, sagaId);
                await storage.Commit();
            }
        }

        #endregion
    }
}
