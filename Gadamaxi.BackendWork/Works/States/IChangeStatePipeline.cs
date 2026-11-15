using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works.States
{
    public interface IChangeStatePipeline
    {
        Task<IWorkState> Handle(ChangeWorkStateContext context, Func<Task<IWorkState>> next);
    }
}
