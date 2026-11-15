using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works
{
    public class WorkManager : IWorkManager
    {
        private readonly ICreatePipelineProvider _createPipelineProvider;
        private readonly ILogger<WorkManager> _logger;
        private readonly IWorkStateHandlerProvider _handlerProvider;
        private readonly IWorkQuery _workQuery;
        private readonly IWorkQueue _workQueue;

        public WorkManager(ILoggerFactory loggerFactory) 
        {
            _createPipelineProvider = DependencyInjectionHelper.Get<ICreatePipelineProvider>();
            _logger = loggerFactory.CreateLogger<WorkManager>();
            _handlerProvider = DependencyInjectionHelper.Get<IWorkStateHandlerProvider>();
            _workQuery = DependencyInjectionHelper.Get<IWorkQuery>();
            _workQueue = DependencyInjectionHelper.Get<IWorkQueue>();
        }

        public async Task<bool> ChangeState(WorkStateContext context)
        {
            var data = await _workQuery.GetWorkData(context.WorkId);
            if (data == null)
            {
                return false;
            }

            await WorkFactory.ChangeState(context.WorkId, context.NewState, data.StateName,data);
            return true;
        }

        public async Task<string> Create(CreateWorkContext context, string name)
        {
            Helper.CheckWorkName(name);

            var pipelines = _createPipelineProvider.GetPipelines();

            Func<Task<string>> next = async () =>
            {
                return await InnerCreate(context, name);
            };


            Func<Func<Task<string>>, Func<Task<string>>> pred;
            foreach (var pip in pipelines.Reverse())
            {
                pred = (nxt) =>
                {
                    return async () =>
                    {
                         return await pip.Handle(context, nxt);
                    };
                };

                next = pred(next);
            }

            var result = await next();

            return result;
        }

        private async Task<string> InnerCreate(CreateWorkContext context, string name)
        {
            await BeginCreate(context);
            string workId = string.Empty;
            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                workId = await WorkFactory.CreateWork(storage, 
                    _workQueue,_handlerProvider, 
                    context.Work, 
                    context.CreateUtcTime, 
                    this.GetType().Name, 
                    context.InitState,
                    context.AddParameters,
                    name);
                await storage!.Commit();
            }
            await EndCreate(context);
            return workId;
        }

        private Task BeginCreate(CreateWorkContext context)
        {
            return Task.CompletedTask;
        }

        private Task EndCreate(CreateWorkContext context)
        {
            return Task.CompletedTask;
        }

        public async Task RemoveWork(string workId)
        {
            Helper.CheckArgumentNull(workId, nameof(workId));
            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                // 先删状态行（子）再删工作行（父）：SqlServer 下存在 State.WorkId → Work.Id 外键，
                // 反序会违反外键约束。SQLite 不建该外键，故此前未暴露；两者语义一致。
                await storage.RemoveWorkState(workId);
                await storage.RemoveWork(workId);
                // 存储写入是延迟事务，不提交则删除不会落库
                await storage.Commit();
            }
        }

        public async Task ReEnqueue(string workId)
        {
            Helper.CheckArgumentNull(workId, nameof(workId));

            var data = await _workQuery.GetWorkData(workId);
            if (data != null)
            {
                await WorkFactory.EnqueuedState(workId,data.StateName,data,string.Empty);
            }
        }
    }
}
