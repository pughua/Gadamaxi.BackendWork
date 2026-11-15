using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Batchs
{
    public interface IBatchStatusPipelineProvider
    {
        IEnumerable<IBatchStatusPipeline> GetPipelines();
    }
}
