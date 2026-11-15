using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Storages
{
    public class ServerInfo
    {
        public ServerInfo() 
        { 
        
        }
        public string ServerName { get; set; } = string.Empty;
        public ServerData Data { get; set; }

        public DateTime HeartTime { get; set; } = Helper.CurrentUtcTime();
    }

    public class ServerData
    {
        public List<string> Queues { get; set; }= new List<string>();

    }
}
