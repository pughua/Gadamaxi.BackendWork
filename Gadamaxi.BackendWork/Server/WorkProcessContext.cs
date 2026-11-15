using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Gadamaxi.BackendWork.Server
{
    public class WorkProcessContext
    {
        public WorkProcessContext(ServerContext server) 
        {
            Server = server;
        }
        public  ServerContext Server { get; private set; }
        public string WorkThreadName { get; set; }
    }
}
