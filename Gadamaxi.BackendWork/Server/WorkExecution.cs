using Gadamaxi.BackendWork.Resources;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    internal class WorkExecution : IWorkExecution
    {
        private readonly ILogger<WorkExecution> _logger;
        public WorkExecution(ILogger<WorkExecution> logger)
        {
            _logger = logger;
        }
        public async Task Run(ServerContext context, IWorkProcess process, int threadIdx)
        {
            Helper.CheckArgumentNull(context, nameof(context));
            Helper.CheckArgumentNull(process,nameof(process));
            // 停止令牌必须在每轮入口检查：各进程的轮询等待已经能被令牌打断，
            // 但循环本身也要能退出，否则服务停止后工作线程会一直空转
            while (!context.StopToken.IsCancellationRequested)
            {
                var error = false;
                var watch = Stopwatch.StartNew();
                try
                {
                    var workContext = new WorkProcessContext(context);
                    workContext.WorkThreadName = $"{process.GetType().Name}-{threadIdx}";
                    await process.Execute(workContext);
                }
                catch(OperationCanceledException ex)
                {
                    if (ex.CancellationToken.Equals(context.StopToken))
                    {
                        break;
                    }
                    else
                    {
                        error = true;
                        _logger.LogWarning(string.Format(Resource.UnknownError, $"{ex.Message} {ex.GetStacks()}"));
                    }
                }
                catch(Exception ex)
                {
                    error = true;
                    _logger.LogWarning(string.Format(Resource.UnknownError, $"{ex.Message} {ex.GetStacks()}"));
                }

                if (process is IOnceWorkProcess)
                {
                    return;
                }

                watch.Stop();
                if (watch.ElapsedMilliseconds <100 && error)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Helper.One));
                }
            }
        }
    }
}
