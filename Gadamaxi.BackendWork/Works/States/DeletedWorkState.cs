using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works.States
{
    public class DeletedWorkState : BaseWorkState
    {
        public const string Name = "deleted";
        public DeletedWorkState(string info)
            :base(Name,info,true)
        {
        }

        public DeletedWorkState(IWorkState state) : base(state, true)
        {

        }

        public override IReadOnlyDictionary<string, string> GetData()
        {
            return _data;
        }
    }

    internal class DeletedWorkStateHandler : IWorkStateHandler
    {
        public string StateName => DeletedWorkState.Name;

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
