using Gadamaxi.BackendWork.Works.States;
using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Works
{
    public class WorkStateContext
    {
        public WorkStateContext(string workId, IWorkState newState, List<IWorkState>? preStates) 
        {
            WorkId = workId;
            NewState = newState;
            PreStates = preStates;
        }
        public string WorkId { get;private set; } 

        public IWorkState NewState { get; private set; }

        public List<IWorkState>? PreStates { get; private set; }
    }
}
