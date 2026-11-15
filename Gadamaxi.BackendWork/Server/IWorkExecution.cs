using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    public interface IWorkExecution
    {
        Task Run(ServerContext context, IWorkProcess process,int threadIdx);
    }
}
