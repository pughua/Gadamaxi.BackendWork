using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works.States
{
    public class EnqueuedWorkState: BaseWorkState
    {
        public const string Name = "enqueued";
        public EnqueuedWorkState(string info,string queue)
            :base(Name,info,false)
        {
            Queue = queue;
        }

        public EnqueuedWorkState(IWorkState state) : base(state,false)
        {
        }

        public string Queue { get; set; } = string.Empty;

        public override IReadOnlyDictionary<string, string> GetData()
        {
            if (!string.IsNullOrEmpty(Queue))
            {
                Add(nameof(Queue), Queue);
            }
            
            return _data;
        }
    }

    internal class EnqueuedWorkStateHandler : IWorkStateHandler
    {
        public string StateName => EnqueuedWorkState.Name;

        public async Task Handle(ChangeWorkStateContext context)
        {
            Helper.CheckArgumentNull(context, nameof(context));
            Helper.CheckArgumentNull(context.WorkId, nameof(context.WorkId));
            if (context.NewState is EnqueuedWorkState state)
            {
                Helper.CheckArgumentNull(state.Queue, nameof(state.Queue));
                // 走存储而不是直接投递到队列：存储的写入是延迟事务，
                // 消息必须与工作行、状态行在同一事务中可见，否则消费端会在工作行落库前
                // 取到消息并因"工作不存在"把消息确认掉，导致任务永久丢失。
                await context.Storage.Publish(new Storages.QueueInfo
                {
                    WorkId = context.WorkId,
                    QueueName = state.Queue,
                });
            }
        }

        public Task RemovedHandle(ChangeWorkStateContext context)
        {
            return Task.CompletedTask;
        }
    }
}
