using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works.States
{
    public class SucceededWorkState : BaseWorkState
    {
        public const string Name = "succeeded";

        public SucceededWorkState(object? result,long durationMillisecconds, string info)
            : base(Name, info,true)
        {
            Result = result;
            DurationMillisecconds = durationMillisecconds;
        }

        public SucceededWorkState(IWorkState state) : base(state, true)
        {

        }

        public object? Result { get; private set; }

        public long DurationMillisecconds { get;private set;} 

        public override IReadOnlyDictionary<string, string> GetData()
        {
            if (Result != null)
            {
                var result = string.Empty;
                try
                {
                    result = Helper.Serialize(Result)!;
                }
                catch (Exception)
                {
                }
                Add(nameof(Result), result);
            }
            return _data;
        }
    }

    internal class SucceededWorkStateHandler : IWorkStateHandler
    {
        public string StateName => SucceededWorkState.Name;

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
