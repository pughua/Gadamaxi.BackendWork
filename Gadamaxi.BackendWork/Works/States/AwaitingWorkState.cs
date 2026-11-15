using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works.States
{
    public class AwaitingWorkState : BaseWorkState
    {
        public const string Name = "awaiting";

        public AwaitingWorkState(string parentId, string info)
            : base(Name, info,false)
        {
            Helper.CheckArgumentNull(parentId,nameof(parentId));
            ParentId = parentId;
        }

        public AwaitingWorkState(IWorkState state) : base(state, false)
        {

        }

        public override IReadOnlyDictionary<string, string> GetData()
        {
            Add(nameof(ParentId), ParentId);
            return _data;
        }

        public string ParentId { get; set; } = string.Empty;
    }

    internal class AwaitingWorkStateHandler : IWorkStateHandler
    {
        public string StateName => AwaitingWorkState.Name;

        public async Task Handle(ChangeWorkStateContext context)
        {
            Helper.CheckArgumentNull(context, nameof(context));
            if (context.NewState is AwaitingWorkState state)
            {
                await context.Storage.AddCacheSet($"{AwaitingWorkState.Name}:{state.ParentId}",
                    state.CreateUtcTime.UnixMillisecondStamp(), context.WorkId);

                await context.Storage.AddCacheSet($"{AwaitingWorkState.Name}:continue:{context.WorkId}",
                    state.CreateUtcTime.UnixMillisecondStamp(), state.ParentId);

                // 父任务可能在子任务创建之前就已经完成（典型场景：批量展开时前一个子工作已先跑完）。
                // 这里必须立即入队 —— 放在状态处理器而不是"创建之后补偿检查"，
                // 是因为补偿检查无法覆盖"父任务完成"与"子任务创建"之间的时间窗口。
                var query = DependencyInjectionHelper.Get<IWorkQuery>();
                if (await WorkFactory.ParentWorkFinished(query, state.ParentId))
                {
                    await WorkFactory.EnqueuedState(context.Storage, context.WorkId,
                        AwaitingWorkState.Name, context.WorkData);
                }
            }
        }

        public async Task RemovedHandle(ChangeWorkStateContext context)
        {
            Helper.CheckArgumentNull(context, nameof(context));
            if (context.OldStateName == AwaitingWorkState.Name)
            {
                var query = DependencyInjectionHelper.Get<IWorkQuery>();
                var ids = await query.GetCacheSet($"{AwaitingWorkState.Name}:continue:{context.WorkId}", Helper.Zero
                    , Helper.CurrentUtcTime().UnixMillisecondStamp(), DependencyInjectionHelper.Get<WorkServerOption>().GetBatchSize());
                foreach(var id in ids)
                {
                    await context.Storage.RemoveCacheSet($"{AwaitingWorkState.Name}:{id}", context.WorkId);
                    await context.Storage.RemoveCacheSet($"{AwaitingWorkState.Name}:continue:{context.WorkId}", id);
                }
            }

        }
    }
}
