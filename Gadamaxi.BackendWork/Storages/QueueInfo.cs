using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Storages
{
    public class QueueInfo
    {
        public string QueueName { get; set; } = string.Empty;
        public string WorkId { get; set; } = string.Empty;

        public override string ToString()
        {
            return $"{QueueName}:{WorkId}";
        }
    }
}
