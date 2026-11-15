using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Gadamaxi.BackendWork.Batchs
{
    public class BatchStatusPipelineProvider : IBatchStatusPipelineProvider
    {
        private readonly List<IBatchStatusPipeline> _statusPipelines;
        public BatchStatusPipelineProvider()
        {
            _statusPipelines = new List<IBatchStatusPipeline>
            {
                new DefaultBatchStatusPipeline()
            };
        }
        public IEnumerable<IBatchStatusPipeline> GetPipelines()
        {
            return _statusPipelines.AsEnumerable();
        }
    }
}
