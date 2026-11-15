using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works.States
{
    public class ProcessingWorkState : BaseWorkState
    {
        public const string Name = "processing";

        public ProcessingWorkState(string serverName, string info)
            :base(Name,info,false)
        {
            Helper.CheckArgumentNull(serverName, nameof(serverName));
            ServerName = serverName;
        }

        public ProcessingWorkState(IWorkState state) : base(state, false)
        {
            ServerName = string.Empty;
        }

        public string ServerName { get; private set; }

        public override IReadOnlyDictionary<string, string> GetData()
        {
            Add(nameof(ServerName), ServerName);
            return _data;
        }
    }

    internal class ProcessingWorkStateHandler : IWorkStateHandler
    {
        public string StateName => ProcessingWorkState.Name;

        public Task Handle(ChangeWorkStateContext context)
        {
            return Task.CompletedTask;
        }

        public Task RemovedHandle(ChangeWorkStateContext context)
        {
            return Task.CompletedTask;
        }
    }
}
