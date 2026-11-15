using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    public interface IWorkServer: IDisposable
    {
        Task Run();

        /// <summary>优雅停机：停取新任务 → 等在途任务完成 → 超时放弃。</summary>
        Task Stop();

        /// <summary>兼容旧命名（StartStop）：请改用 <see cref="Stop"/>。</summary>
        [Obsolete("命名修正：请改用 Stop()。")]
        Task StartStop();
    }
}
