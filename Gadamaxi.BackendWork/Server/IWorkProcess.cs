using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    public interface IWorkProcess
    {
        public Task Execute(WorkProcessContext context);
    }

    public interface IOnceWorkProcess: IWorkProcess
    {

    }
}
