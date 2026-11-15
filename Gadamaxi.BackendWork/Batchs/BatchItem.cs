using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Batchs
{
    public class BatchItem
    {
        public BatchItem() 
        {
            Id = Helper.NewId();
            Status = BatchStatus.Init;
            Name = string.Empty;
        }

        public string Id { get; set; } = string.Empty;

        /// <summary>人工可识别的子任务名，由批量名加序号派生（形如 "月结对账#1"）</summary>
        public string Name { get; set; }

        public string? WorkId { get; set; } = string.Empty;

        public BatchStatus Status { get; set; }

        public string Message { get; set; } = string.Empty;

        public Workload Work { get; set; }

        public BatchItemData Data { get; set; }

    }
}
