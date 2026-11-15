using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Storages
{
    public interface IWorkQueue
    {
        Task Publish(QueueInfo info);
        Task Subscribe(List<string> queues,Func<QueueSubscribeContext, Task> subscribeAction);
    }
}
