using Gadamaxi.BackendWork.Resources;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works
{
    public class StateCreatePipeline : ICreatePipeline
    {
        public int Order { get; set; }

        private readonly IWorkQuery _workQuery;

        public StateCreatePipeline()
        {
            _workQuery = DependencyInjectionHelper.Get<IWorkQuery>();
        }

        public async Task<string> Handle(CreateWorkContext context, Func<Task<string>> next)
        {
            var result = await next();

            await AwaitingStateHandle(context, result);

            return result;
        }

        private async Task AwaitingStateHandle(CreateWorkContext context,string workId)
        {
            if(context.InitState is AwaitingWorkState state)
            {
                // 仅在仍处于 awaiting 时才补偿入队：正常情况下 awaiting 状态处理器已处理过，
                // 重复入队会让同一个工作被投递两次
                await WorkFactory.EnqueueAwaitingWorkIfParentFinished(_workQuery, workId, state.ParentId);
            }
        }
    }
}
