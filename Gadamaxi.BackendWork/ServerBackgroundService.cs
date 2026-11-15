using Gadamaxi.BackendWork.Server;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork
{
    public class ServerBackgroundService : IHostedService, IDisposable
    {
        private readonly IWorkServer _workServer;

        public ServerBackgroundService(IWorkServer workServer)
        {
            _workServer = workServer;
        }
        public void Dispose()
        {
            _workServer.Dispose();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _workServer.Run();
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _workServer.Stop();
        }
    }
}
