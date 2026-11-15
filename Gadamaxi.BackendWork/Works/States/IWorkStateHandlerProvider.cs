using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works.States
{
    public interface IWorkStateHandlerProvider
    {
        Task<IWorkState> Run(ChangeWorkStateContext context);
    }
}
