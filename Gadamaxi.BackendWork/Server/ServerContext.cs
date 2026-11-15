using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    public class ServerContext
    {
        public ServerContext(CancellationToken stopToken, 
            IWorkDispatcher workDispatcher,
            IReadOnlyList<IWorkProcess> workProcesses) 
        {
            StopToken = stopToken;
            WorkDispatcher = workDispatcher;
            WorkProcesses = workProcesses;
            TaskScheduler = TaskScheduler.Default;
        }
        public CancellationToken StopToken { get; private set; }
        public string ServerName { get; set; } = string.Empty;
        public IWorkDispatcher WorkDispatcher { get; private set; }
        public IReadOnlyList<IWorkProcess> WorkProcesses { get; private set; }
        public int WorkThreads { get; set; }
        public List<string> Queues { get; set; } = new List<string>();
        public TaskScheduler TaskScheduler { get; set; }

        private int _inFlight;

        /// <summary>
        /// 当前正在执行的任务数（在途任务）。优雅停机（#9）时用于观察在途任务是否跑完。
        /// </summary>
        public int InFlightCount => Volatile.Read(ref _inFlight);

        /// <summary>标记一个任务开始执行（在途任务数 +1）</summary>
        public void BeginWork() => Interlocked.Increment(ref _inFlight);

        /// <summary>标记一个任务执行结束（在途任务数 -1）</summary>
        public void EndWork() => Interlocked.Decrement(ref _inFlight);
    }
}
