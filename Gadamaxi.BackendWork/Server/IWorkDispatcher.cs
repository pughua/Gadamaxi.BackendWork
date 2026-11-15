using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    public interface IWorkDispatcher
    {
        Task Dispatch(ServerContext context, IWorkProcess process,int workCount);

        /// <summary>
        /// 等待所有已派发的工作循环退出，最多等待 <paramref name="timeout"/>。
        /// 返回是否在超时前全部退出（false 表示超时，仍有在途任务）——优雅停机（#9）据此决定放弃等待。
        /// </summary>
        Task<bool> WaitForExitAsync(TimeSpan timeout);
    }

    public static class WorkDispatcherExtention
    {
        public static async Task Dispatch(this IWorkDispatcher dispatcher, ServerContext context, IWorkProcess process)
        {
            await dispatcher.Dispatch(context, process, 1);
        }
    }
}
