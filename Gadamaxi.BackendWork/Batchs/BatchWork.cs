using Gadamaxi.BackendWork.Server;
using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Batchs
{
    public class BaseBatchWork
    {
        public BaseBatchWork()
        {
            BatchId = Helper.NewId();
            Status = BatchStatus.None;
            RetryCount = Helper.Zero;
            Message = string.Empty;
            Name = string.Empty;
            ExpireUtcTime = Helper.CurrentUtcTime().Add(Helper.DefaultBatchExpireUtcTime);
            ParentBatchId = string.Empty;
        }

        public string BatchId { get; set; }
        public string ParentBatchId { get; set; }

        /// <summary>人工可识别的批量名（监控面板使用）</summary>
        public string Name { get; set; }
        public BatchStatus Status { get; set; }
        public string Message { get; set; }
        public int RetryCount { get; set; }

        public int Count { get; set; }

        public int DealCount { get; set; }

        public DateTime ExpireUtcTime { get; set; }

        public bool Finish()
        {
            if (Status == BatchStatus.Success)
            {
                return true;
            }

            return false;
        }
    }

    public class BatchWork: BaseBatchWork
    {
        public BatchWork():base() 
        {
            Items = new List<BatchItem>();
        }
        public List<BatchItem> Items { get; set; }
    }
}
