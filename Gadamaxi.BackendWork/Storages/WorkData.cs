using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Storages
{
    public class WorkData
    {
        public WorkData() 
        {
            CreatedUctTime = Helper.CurrentUtcTime();
            Parameters = new Dictionary<string, string>();
        }

        public string WorkId { get; set; } = string.Empty;

        /// <summary>人工可识别的任务名（监控面板使用）</summary>
        public string Name { get; set; } = string.Empty;

        public string StateName { get; set; } = string.Empty;

        public WorkloadContent Content { get; set; }

        public DateTime CreatedUctTime { get; set; }

        public DateTime? ExpireUtcTime { get; set; }

        public Dictionary<string, string> Parameters { get; set; }
    }
}
