using Gadamaxi.BackendWork.Storages;
using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Batchs
{
    public class BatchChangeStatueContext
    {
        public BatchChangeStatueContext(IWorkStorage storage
            , BaseBatchWork batchWork
            , BatchStatus oldStatus) 
        {
            Storage = storage;
            BatchWork = batchWork;
            OldStatus = oldStatus;
        }

        public IWorkStorage Storage { get; private set; }
        public BaseBatchWork BatchWork { get; private set; }
        public BatchStatus OldStatus { get; private set; }
    }
}
