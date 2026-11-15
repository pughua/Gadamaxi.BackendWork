using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works.States
{
    internal class DefaultChangeStatePipeline : IChangeStatePipeline
    {
        private readonly IWorkQuery _workQuery;
        private readonly WorkServerOption _option;
        private readonly ILogger<DefaultChangeStatePipeline> _logger;
        public DefaultChangeStatePipeline()
        {
            _workQuery = DependencyInjectionHelper.Get<IWorkQuery>();
            _option = DependencyInjectionHelper.Get<WorkServerOption>();
            _logger = DependencyInjectionHelper.Get<ILogger<DefaultChangeStatePipeline>>();
        }

        public async Task<IWorkState> Handle(ChangeWorkStateContext context, Func<Task<IWorkState>> next)
        {
            var result = await next();
            await After(context);
            return result;
        }

        private async Task After(ChangeWorkStateContext context)
        {
            await FailedRetry(context);
            await Success2AwaitingWork(context);
        }

        private async Task FailedRetry(ChangeWorkStateContext context)
        {
            if (context.NewState is FailedWorkState)
            {
                var count = await _workQuery.CountState(context.WorkId, FailedWorkState.Name);
                // 与批量项终态回写共用同一条规则，避免两处对"是否还会重试"判断不一致
                if (!WorkFactory.RetryExhausted(count))
                {
                    var rcount = count + Helper.One;
                    var dcount = rcount * count;
                    if (count <= Helper.Zero)
                    {
                        dcount = rcount;
                    }
                    var delay = _option.WorkRetryDelay * dcount;
                    var state = new ScheduledWorkState(delay, string.Format(Resources.Resource.RetryInfo, $"{rcount}/{_option.WorkRetryCount}"));
                    await WorkFactory.ChangeState(context.Storage,
                        context.WorkId, state,
                        context.NewState.StateName, context.WorkData);

                    _logger.LogInformation($"Work {context.WorkId} will retry {rcount}/{_option.WorkRetryCount},delay {delay}.");
                }
            }
        }

        private async Task Success2AwaitingWork(ChangeWorkStateContext context)
        {
            if (!context.NewState.Finish)
            {
                return;
            }
            // 与"父任务提交后再唤醒一次"共用同一实现：
            // 这里的扫描发生在状态落库之前，可能漏掉在扫描之后才完成注册的子任务，
            // 那部分子任务由提交后的第二次唤醒兜底。
            await WorkFactory.EnqueueAwaitingChildren(_workQuery, context.WorkId, context.Storage);
        }
    }
}
