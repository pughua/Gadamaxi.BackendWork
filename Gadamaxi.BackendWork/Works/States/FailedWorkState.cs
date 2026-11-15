using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works.States
{
    public class FailedWorkState : BaseWorkState
    {
        public const string Name = "failed";

        public FailedWorkState(Exception ex, long elapsedMilliseconds,string serverName, string info)
            :base(Name,info,false)
        {
            Helper.CheckArgumentNull(serverName, nameof(serverName));
            ServerName = serverName;
            Exception = ex;
            ElapsedMilliseconds = elapsedMilliseconds;
        }

        public FailedWorkState(IWorkState state) : base(state, false)
        {
            
        }

        public string ServerName { get; private set; }
        public Exception Exception { get; private set; }
        public long ElapsedMilliseconds { get; private set; }

        public override IReadOnlyDictionary<string, string> GetData()
        {
            Add($"{nameof(Exception)}Message", Exception.Message);
            Add($"{nameof(Exception)}Stacks", Exception.GetStacks());
            Add(nameof(ServerName), ServerName);
            Add(nameof(ElapsedMilliseconds), ElapsedMilliseconds.ToString());
            return _data;
        }
    }
    internal class FailedWorkStateHandler : IWorkStateHandler
    {
        public string StateName => FailedWorkState.Name;

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
