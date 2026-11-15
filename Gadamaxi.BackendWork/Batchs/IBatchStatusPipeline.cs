using Gadamaxi.BackendWork.Works.States;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Batchs
{
    public interface IBatchStatusPipeline
    {
        Task Handle(BatchChangeStatueContext context, Func<Task> next);
    }
}
