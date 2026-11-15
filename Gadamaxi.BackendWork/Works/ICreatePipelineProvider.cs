using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Works
{
    public interface ICreatePipelineProvider
    {
        IEnumerable<ICreatePipeline> GetPipelines();
    }
}
