using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Storages
{
    public class QueueSubscribeContext
    {
        public QueueSubscribeContext(QueueInfo info) 
        {
            Helper.CheckArgumentNull(info, nameof(info));
            Info = info;
        }
        public QueueInfo Info { get; }
        public bool DealSuccess { get; set; } = false;
    }
}
