using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works
{
    public interface ICreatePipeline
    {
        int Order { get; set; }
        Task<string> Handle(CreateWorkContext context, Func<Task<string>> next);
    }
}
