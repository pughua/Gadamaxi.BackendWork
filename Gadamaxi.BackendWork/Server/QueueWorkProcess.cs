using Gadamaxi.BackendWork.Resources;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    internal class QueueWorkProcess : IWorkProcess
    {
        private readonly IWorkQueue _workQueue;
        private readonly ILogger<QueueWorkProcess> _logger;
        private readonly IDistributedLock _distributedLock;
        private readonly IWorkQuery _workQuery;
        private readonly WorkServerOption _option;
        public QueueWorkProcess(ILogger<QueueWorkProcess> logger) 
        {
            _workQueue = DependencyInjectionHelper.Get<IWorkQueue>(); ;
            _logger = logger;
            _workQuery = DependencyInjectionHelper.Get< IWorkQuery>();
            _distributedLock = DependencyInjectionHelper.Get< IDistributedLock>();
            _option = DependencyInjectionHelper.Get<WorkServerOption>();
        }

        public async Task Execute(WorkProcessContext context)
        {
            var count = Helper.Zero;
            await _workQueue.Subscribe(context.Server.Queues, async (ctx) =>
            {
                // 停机已发起：本轮取到的消息不再执行，消息保留待可见性超时后重新投递（#9/#16）
                if (context.Server.StopToken.IsCancellationRequested)
                {
                    return;
                }

                var success = await Perform(context,ctx.Info);
                ctx.DealSuccess = success;
                count++;
                // 消息间隔不再是无取消令牌的硬编码 Task.Delay：停机时能立刻被唤醒（#16）。
                // works 传 One(>0)，避免走到"无任务时放大间隔"的分支。
                try
                {
                    await Helper.Delay(_option.WorkIntervalDelay, Helper.DefaultWorkIntervalDelay, Helper.One, context.Server.StopToken);
                }
                catch (OperationCanceledException)
                {
                    // 等待被停机打断。取消只意味着"不用再等了"，本条消息已确认的结果不受影响，
                    // 因此吞掉异常让 Subscribe 正常走到"确认后删除消息"分支，
                    // 否则消息会被当成处理失败保留下来（停机前已完成的工作被重复投递）。
                }
            });

            if (count <= Helper.Zero)
            {
                _logger.LogTrace($"{context.WorkThreadName} no work was obtained.");
                await Helper.Delay(_option.WorkPollingDelay, Helper.DefaultWorkPollingDelay, count, context.Server.StopToken);
            }
        }

        internal async Task<bool> Perform(WorkProcessContext context,QueueInfo info)
        {
            using (await _distributedLock.AcquireLock(info.WorkId, Helper.DistributedLockTimeOut))
            {
                // 登记在途任务：优雅停机（#9）据此观察是否还有任务在跑
                context.Server.BeginWork();
                try
                {
                    // 注意：这里不对整个任务执行做基础设施重试。
                    // - 任务失败：由 FailedWorkState -> ScheduledWorkState 状态机按次数退避重试，保证任务方法只执行一次；
                    // - 基础设施异常：抛出后由消息可见性超时（WorkQueue.Subscribe）重新投递，
                    //   重新投递时 processing 状态检查保证不会重复执行任务方法。
                    return await PerformMethod(context, info);
                }
                finally
                {
                    context.Server.EndWork();
                }
            }
        }

        internal async Task<bool> PerformMethod(WorkProcessContext context, QueueInfo info)
        {
            var success = false;
            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                var watch = Stopwatch.StartNew();
                var data = await _workQuery.GetWorkData(info.WorkId);
                if (data == null)
                {
                    // 工作已被删除，确认消息出队
                    return true;
                }

                var lastState = await _workQuery.GetLastState(info.WorkId);
                if (lastState != null && lastState.Finish)
                {
                    _logger.LogTrace($"Work {info} has been completed and does not need to be executed.");
                    success = true;
                    return success;
                }

                if (lastState is ProcessingWorkState)
                {
                    // 可见性超时重投场景：任务正在其它 worker 执行（或等待卡死恢复进程处理），
                    // 直接确认消息，避免重复执行任务方法；执行者崩溃时由 RecoveryWorkProcess 负责恢复。
                    _logger.LogTrace($"Work {info} is processing by another worker, skip.");
                    success = true;
                    return success;
                }

                await ChangeProcessingState(context, info, data);
                try
                {
                    var work = Workload.Deserialize(data.Content);

                    object? result = null;
                    using (var act = new WorkActivator(work))
                    {
                        result = await act.Invoke();
                    }

                    watch.Stop();

                    var successState = new SucceededWorkState(result,watch.ElapsedMilliseconds,string.Format(Resource.TriggerBy, nameof(QueueWorkProcess)));
                    
                    await WorkFactory.ChangeState(storage, data.WorkId, successState, data.StateName,data);
                    success = true;
                    _logger.LogInformation($" Executed work {info} success.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"An error occurred while executing job {info}.{ex.Message}\n{ex.GetStacks()}");
                    if (ex is OutOfMemoryException)
                    {
                        throw ex;
                    }
                    watch.Stop();
                    // 解包反射调用的包装异常再落库：MethodInfo.Invoke 会把「同步方法」抛出的业务异常
                    // 包进 TargetInvocationException（异步方法 await 后抛的是原异常，不受影响），
                    // 直接记录会让失败原因变成「Exception has been thrown by the target of an invocation.」
                    // 这种零信息量的文本。而这条原因会被写进 Work 状态数据（ExceptionMessage）、
                    // 批量子任务消息与 saga 步骤消息（进而成为 saga 的中止原因），
                    // 是监控面板与人工介入时唯一的依据，必须是业务根因。
                    var cause = ex;
                    while (cause is TargetInvocationException && cause.InnerException != null)
                    {
                        cause = cause.InnerException;
                    }

                    var failState = new FailedWorkState(cause, watch.ElapsedMilliseconds, context.Server.ServerName, string.Empty);

                    await WorkFactory.ChangeState(storage, data.WorkId, failState, data.StateName,data);
                    // 失败已写入状态机（FailedWorkState -> ScheduledWorkState 退避重试），
                    // 确认消息出队，避免消息重投绕过退避延迟造成额外的重复执行
                    success = true;
                    _logger.LogInformation($" Execute work {info} failure.");
                }

                await storage.Commit();
            }

            // 状态落库之后再唤醒一次连续子任务，消除丢失唤醒：
            // 子任务可能在"父任务扫描等待集合"之后、但在父任务完成状态落库之前完成注册，
            // 此时上面的扫描会漏掉它，而子任务自己检查时父任务也尚未完成，任务将永久等待。
            if (success)
            {
                await WorkFactory.EnqueueAwaitingChildren(_workQuery, info.WorkId);
            }

            return success;
        }

        private async Task ChangeProcessingState(WorkProcessContext context, QueueInfo info,WorkData data)
        {
            using(var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                var state = new ProcessingWorkState(context.Server.ServerName, string.Format(Resource.TriggerBy, nameof(QueueWorkProcess)));
                await WorkFactory.ChangeState(storage, data.WorkId, state, data.StateName,data);
                await storage.Commit();
            }

        }
    }
}
