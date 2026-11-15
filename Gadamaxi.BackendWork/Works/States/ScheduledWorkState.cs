using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works.States
{
    public class ScheduledWorkState: BaseWorkState
    {
        public const string Name = "scheduled";
        public ScheduledWorkState(TimeSpan delay,string info):
            base(Name,info,false)
        {
            DelayTime = delay;
        }

        public ScheduledWorkState(IWorkState state) : base(state, false)
        {

        }

        public TimeSpan DelayTime { get; private set; }

        public override IReadOnlyDictionary<string, string> GetData()
        {
            Add(nameof(DelayTime),DelayTime.TotalSeconds.ToString());
            return _data;
        }
    }

    internal class ScheduledWorkStateHandler : IWorkStateHandler
    {
        public string StateName => ScheduledWorkState.Name;
        
        public async Task Handle(ChangeWorkStateContext context)
        {
            Helper.CheckArgumentNull(context, nameof(context));
            if (context.NewState is ScheduledWorkState state)
            {
                await context.Storage.AddCacheSet(ScheduledWorkState.Name,
                    state.CreateUtcTime.Add(state.DelayTime).UnixMillisecondStamp(), context.WorkId);
            }
        }

        public async Task RemovedHandle(ChangeWorkStateContext context)
        {
            Helper.CheckArgumentNull(context, nameof(context));
            await context.Storage.RemoveCacheSet(ScheduledWorkState.Name, context.WorkId);
        }
    }
}
