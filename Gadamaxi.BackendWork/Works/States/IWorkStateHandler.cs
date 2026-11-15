using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works.States
{
    internal interface IWorkStateHandler
    {
        Task Handle(ChangeWorkStateContext context);

        Task RemovedHandle(ChangeWorkStateContext context);

        string StateName { get; }
    }
}
