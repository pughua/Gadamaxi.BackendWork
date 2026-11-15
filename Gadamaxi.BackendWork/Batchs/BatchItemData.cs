using Gadamaxi.BackendWork.Works.States;
using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Batchs
{
    public abstract class BatchItemData
    {
    }

    public class BatchItemWorkData: BatchItemData
    {
        public WorkStateInfo State { get; set; } = new WorkStateInfo();
        public Dictionary<string, string> Parameters { get; set; }= new Dictionary<string, string>();
    }

    public class WorkStateInfo
    {
        public string StateName { get; set; } = string.Empty;
        public string Info { get; set; } = string.Empty;
        public DateTime CreateUtcTime { get; set; }
        public Dictionary<string, string> Data { get; set; } = new Dictionary<string, string>();
    }
}
