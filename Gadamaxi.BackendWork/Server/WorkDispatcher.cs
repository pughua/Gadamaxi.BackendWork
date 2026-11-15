using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    internal class WorkDispatcher: IWorkDispatcher
    {
        private readonly IWorkExecution _execution;
        private readonly ILogger<WorkDispatcher> _logger;
        /// <summary>
        /// 已派发工作循环的句柄，供优雅停机等待其真正退出（#9）。
        /// 修复前是 new Thread(async void 委托) 配一个 TCS 才能被观察；
        /// 现在工作循环是普通 Task（#16），委托里的异常与生命周期都能直接 await，
        /// 不再需要常驻线程 + TCS 这套间接手段。
        /// </summary>
        private readonly ConcurrentBag<Task> _workers = new ConcurrentBag<Task>();
        public WorkDispatcher(IWorkExecution execution,
            ILogger<WorkDispatcher> logger) 
        {
            _execution = execution;
            _logger = logger;
        }

        public async Task Dispatch(ServerContext context, IWorkProcess process, int workCount)
        {
            var random = new Random(Helper.CurrentUtcTime().Millisecond);
            Helper.CheckMiniValueError(Helper.One, workCount, nameof(workCount));
            for (var i = 0; i < workCount; i++)
            {
                // 工作循环主体是异步的（DB 查询 + 轮询等待都是 await），
                // 用 Task.Run 交给线程池即可：不再为每个循环常驻一条专用线程（#16）。
                // Task.Run(Func<Task>) 会自动解包，得到的 Task 即代表整个循环生命周期。
                var worker = Task.Run(async () =>
                {
                    try
                    {
                        await _execution.Run(context, process, i);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, ex.Message);
                    }
                });
                _workers.Add(worker);
                await Task.Delay(random.Next(200, 1000));
            }
        }

        /// <summary>
        /// 等待所有已派发的工作线程退出，最多等待 <paramref name="timeout"/>。
        /// 返回是否在超时前全部退出（false 表示超时，仍有在途任务）。
        /// </summary>
        public async Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            var workers = _workers.ToArray();
            if (workers.Length <= Helper.Zero)
            {
                return true;
            }

            var all = Task.WhenAll(workers);
            var finished = await Task.WhenAny(all, Task.Delay(timeout));
            return finished == all;
        }
    }
}
