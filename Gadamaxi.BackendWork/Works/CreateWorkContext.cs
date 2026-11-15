using Gadamaxi.BackendWork.Works.States;
using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Works
{
    public class CreateWorkContext
    {
        public CreateWorkContext(Workload work,
            IWorkState initState) 
        { 
            Helper.CheckArgumentNull(initState, nameof(initState));
            Helper.CheckArgumentNull(work,nameof(work));
            Work = work;
            InitState = initState;
            AddParameters = new Dictionary<string,object>();
            CreateUtcTime = Helper.CurrentUtcTime();
        }
        public Workload Work { get; private set; }
        public IWorkState InitState { get; private set; }
        public IDictionary<string, object> AddParameters { get; private set; }

        public DateTime CreateUtcTime { get;private set; }
    }
}
