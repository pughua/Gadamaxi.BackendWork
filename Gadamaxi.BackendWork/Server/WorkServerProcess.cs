using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    internal class WorkServerProcess : IWorkServerProcess
    {
        private readonly ILogger<WorkServerProcess> _logger;
        private readonly ILoggerFactory _loggerFactory;
        public WorkServerProcess(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger< WorkServerProcess>();
            _loggerFactory = loggerFactory;
        }

        public async Task Execute(WorkProcessContext context)
        {
            await RegisterServer(context);
            await RunInner(context.Server);
        }

        private async Task RegisterServer(WorkProcessContext context)
        {
            using(var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                await storage.AddServer(new ServerInfo
                {
                    ServerName = context.Server.ServerName,
                    HeartTime = Helper.CurrentUtcTime(),
                    Data = new ServerData
                    {
                        Queues = context.Server.Queues
                    }
                });
                await storage.Commit();
            }
        }

        private async Task RunInner(ServerContext context)
        {
            var recurringProcess = new RecurringWorkProcess(
                    _loggerFactory.CreateLogger<RecurringWorkProcess>());
            await context.WorkDispatcher.Dispatch(context, recurringProcess);

            // context.WorkThreads 由 WorkServer 从 WorkServerOption.WorkerCount 赋值；
            // 未配置（<1）时才回退到自动推算（#15）
            var threadCount = context.WorkThreads;
            if (threadCount < Helper.One)
            {
                threadCount = Math.Min(Environment.ProcessorCount * 2, Helper.DefaultMaxWorkThreads);
            }

            var queueProcess = new QueueWorkProcess(_loggerFactory.CreateLogger<QueueWorkProcess>());
            await context.WorkDispatcher.Dispatch(context, queueProcess, threadCount);

            var delayProcess = new DelayedWorkProcess(_loggerFactory.CreateLogger<DelayedWorkProcess>());
            await context.WorkDispatcher.Dispatch(context, delayProcess);

            // 卡死任务恢复：执行节点崩溃后 processing 状态的任务由该进程重新入队
            var recoveryProcess = new RecoveryWorkProcess(_loggerFactory.CreateLogger<RecoveryWorkProcess>());
            await context.WorkDispatcher.Dispatch(context, recoveryProcess);

            await context.WorkDispatcher.Dispatch(context, new ServerHeartbeatProcess(_loggerFactory.CreateLogger<ServerHeartbeatProcess>()));

            // 过期数据清理：删除已过期且终态的工作/批量及其从表行，避免表无限膨胀（#20）
            await context.WorkDispatcher.Dispatch(context, new ExpiredDataCleanupProcess(_loggerFactory.CreateLogger<ExpiredDataCleanupProcess>()));

            foreach (var process in context.WorkProcesses)
            {
                await context.WorkDispatcher.Dispatch(context, process);
            }

            //out
            var workProcesses = DependencyInjectionHelper.Get<IEnumerable<IWorkProcess>>(false);
            if(workProcesses != null)
            {
                foreach (var process in workProcesses)
                {
                    await context.WorkDispatcher.Dispatch(context, process);
                }
            }
        }
    }
}
