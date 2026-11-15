using Gadamaxi.BackendWork.Storages;
using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork
{
    public interface IConfig
    {
        Func<IServiceProvider, IDistributedLock> DistributedLockFunc { get; set; }
        Func<IServiceProvider, IWorkQuery> WorkQueryFunc { get; set; }
        Func<IServiceProvider, IWorkQueue> WorkQueueFunc { get; set; }
        Func<IServiceProvider, IWorkStorage> WorkStorageFunc { get; set; }
    }

    public class Config : IConfig
    {
        public Func<IServiceProvider, IDistributedLock> DistributedLockFunc { get; set; }
        public Func<IServiceProvider, IWorkQuery> WorkQueryFunc { get ; set ; }
        public Func<IServiceProvider, IWorkQueue> WorkQueueFunc { get ; set; }
        public Func<IServiceProvider, IWorkStorage> WorkStorageFunc { get; set; }
    }
}
