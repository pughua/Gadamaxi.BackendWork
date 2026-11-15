using Gadamaxi.BackendWork.Storages;
using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Works.States
{
    public class ChangeWorkStateContext
    {
        public ChangeWorkStateContext(
            IWorkStorage storage,
            IWorkQueue workQueue,
            string workId,
            IWorkState newState,
            string? oldStateName,
            Workload work,
            WorkData data
            )
        {
            Storage = storage;
            WorkId = workId;
            NewState = newState;
            OldStateName = oldStateName;
            WorkQueue = workQueue;
            Work = work;
            WorkData = data;

        }
        public IWorkStorage Storage { get; }
        public string WorkId { get; }
        public IWorkState NewState { get; }
        public string? OldStateName { get; }
        public IWorkQueue WorkQueue { get; }

        public Workload Work { get; }

        public WorkData WorkData { get; }
    }
}
