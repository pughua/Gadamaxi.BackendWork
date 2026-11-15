using Gadamaxi.BackendWork.Storages;
using LinqToDB;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Sql
{
    public class DistributedLock : IDistributedLock
    {
        public DistributedLock(ILoggerFactory loggerFactory)
        {
        }
        public async Task<IDisposable> AcquireLock(string key, TimeSpan timeout)
        {
            var options = DependencyInjectionHelper.Get<DataOptions>();
            var factory = DependencyInjectionHelper.Get<ILoggerFactory>();
            var provider = new DistributedLockProvider(options, factory.CreateLogger<DistributedLockProvider>(),key,timeout);
            await provider.AcquireLock();
            return provider;
        }
    }
}
