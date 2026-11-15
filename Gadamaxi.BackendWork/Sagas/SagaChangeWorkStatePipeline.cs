using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// 步骤工作的状态回写管线（结构照 <c>BatchChangeWorkStatePipeline</c>）：
    /// 步骤工作到达终态时把结果回写到 <c>SagaStep</c>，并在同一事务里唤醒调度缓存。
    /// <para>
    /// 本管线与批量项回写共用 <see cref="WorkCompletionResolver"/>，因此"是否真的结束、算成功还是失败"
    /// 的口径必然一致（尤其 <c>FailedWorkState.Finish</c> 恒为 false、终态失败只能靠重试耗尽判定这一点）。
    /// </para>
    /// </summary>
    public class SagaChangeWorkStatePipeline : IChangeStatePipeline
    {
        private readonly IWorkQuery _workQuery;
        private readonly ILogger<SagaChangeWorkStatePipeline> _logger;

        public SagaChangeWorkStatePipeline(IWorkQuery workQuery, ILogger<SagaChangeWorkStatePipeline> logger)
        {
            _workQuery = workQuery;
            _logger = logger;
        }

        public async Task<IWorkState> Handle(ChangeWorkStateContext context, Func<Task<IWorkState>> next)
        {
            var result = await next();

            var sagaId = context.WorkData.Parameters.GetValueOrDefault(Helper.SagaIdKey);
            var stepId = context.WorkData.Parameters.GetValueOrDefault(Helper.SagaStepIdKey);
            if (string.IsNullOrEmpty(sagaId) || string.IsNullOrEmpty(stepId))
            {
                // 不是 saga 步骤工作，放行（普通任务 / 批量子任务都走这里）
                return result;
            }

            var isCompensate = string.Equals(
                context.WorkData.Parameters.GetValueOrDefault(Helper.SagaCompensateKey),
                Helper.One.ToString(), StringComparison.Ordinal);

            var completion = await WorkCompletionResolver.Resolve(_workQuery, context);
            if (completion == WorkCompletion.None)
            {
                // 仍在重试链路中：工作马上会被改写成延时重试，此时不能回写终态，
                // 否则 saga 会在步骤还没真正失败时就转入补偿
                return result;
            }

            var failed = completion == WorkCompletion.Failed;

            // 无条件回写（幂等目标状态）。这里刻意不用 CAS：一次 Commit 内的延迟写入是同一个事务，
            // 若回写因"已被别的写入改过"而抛异常，会把同一事务里的工作状态变更一起回滚，
            // 而工作确实已到终态。CAS 只用在校标/状态推进（必须严格一次）的地方。
            await context.Storage.UpdateSagaStep(sagaId, stepId, ResolveStepStatus(completion, isCompensate),
                failed ? WorkCompletionResolver.ResolveFailureMessage(context) : null, null, isCompensate);

            if (!failed && !isCompensate)
            {
                await CaptureOutput(context, sagaId);
            }

            // 同事务唤醒 saga：下一步立刻可被扫描拾取，不必等满一个 SagaPollingDelay。
            // 与步骤状态在同一事务提交，"步骤已终态"与"可以推进"不会只成功一半。
            await context.Storage.UpdateCacheSet(Helper.SagaKey,
                Helper.CurrentUtcTime().UnixMillisecondStamp(), sagaId);

            return result;
        }

        private static SagaStepStatus ResolveStepStatus(WorkCompletion completion, bool isCompensate)
        {
            if (isCompensate)
            {
                return completion == WorkCompletion.Failed
                    ? SagaStepStatus.CompensationFailed
                    : SagaStepStatus.Compensated;
            }

            return completion == WorkCompletion.Failed
                ? SagaStepStatus.Failed
                : SagaStepStatus.Succeeded;
        }

        /// <summary>
        /// 采集正向步骤的返回值到变量池（变量池随后续步骤的 <c>${key}</c> 占位符供后续步骤使用）。
        /// </summary>
        private async Task CaptureOutput(ChangeWorkStateContext context, string sagaId)
        {
            var outputKey = context.WorkData.Parameters.GetValueOrDefault(Helper.SagaOutputKeyKey);
            if (string.IsNullOrEmpty(outputKey))
            {
                return;
            }

            // 返回值本身就是"这个工作成功状态的产物"，直接取状态对象上的结果，不必再查一次状态表
            var result = (context.NewState as SucceededWorkState)?.Result;
            if (result == null)
            {
                // 无返回值：保留变量池里的既有值，不写入"空"
                return;
            }

            var saga = await _workQuery.GetSagaById(sagaId);
            if (saga == null)
            {
                return;
            }

            saga.Variables[outputKey] = Helper.Serialize(result) ?? string.Empty;

            // 列级更新：本事务只回写变量池，不能整行 Update 掉并发的游标推进
            await context.Storage.UpdateSagaVariables(sagaId, saga.Variables);
            _logger.LogInformation($"Saga {sagaId} variable '{outputKey}' captured from work {context.WorkId}.");
        }
    }
}
