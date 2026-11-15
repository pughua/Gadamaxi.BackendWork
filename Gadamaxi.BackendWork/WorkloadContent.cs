using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork
{
    public class WorkloadContent
    {
        public WorkloadContent() {
        
        }

        public WorkloadContent(string type,
            string method, 
            string parameterTypes, 
            string arguments, 
            string queue)
        {
            Type = type;
            Method = method;
            ParameterTypes = parameterTypes;
            Arguments = arguments;
            Queue = queue;
        }

        public string Type { get; private set; } = string.Empty;
        public string Method { get; private set; } = string.Empty;
        public string ParameterTypes { get; private set; } = string.Empty;
        public string Arguments { get; private set; } = string.Empty;
        public string Queue { get; private set; } = string.Empty;
    }
}
