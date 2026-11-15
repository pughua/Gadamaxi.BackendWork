using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Gadamaxi.BackendWork.Works
{
    public class CreatePipelineProvider : ICreatePipelineProvider
    {
        private List<ICreatePipeline> _pipelines;
        public CreatePipelineProvider() 
        {
            _pipelines = new List<ICreatePipeline>()
            {
                new StateCreatePipeline()
            };
        }
        public IEnumerable<ICreatePipeline> GetPipelines()
        {
            return _pipelines.OrderBy(p => p.Order);
        }
    }
}
