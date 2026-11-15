using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Storages
{
    public interface IDistributedLock
    {
        Task<IDisposable> AcquireLock(string key,TimeSpan timeout);
    }
}
