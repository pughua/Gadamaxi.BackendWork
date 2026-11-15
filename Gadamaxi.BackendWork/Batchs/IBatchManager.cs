using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Batchs
{
    public interface IBatchManager
    {
        /// <summary>
        /// 创建批量任务。name 为人工可识别的批量名（监控面板使用），必传；
        /// 批量子任务名由批量名加序号派生（形如 "月结对账#1"）。
        /// </summary>
        Task<string> Create(Action<BatchContext> methodCall, string name);

        /// <summary>在已有批量任务后继续追加一个批量任务，name 规则同 <see cref="Create"/></summary>
        Task<string> CreateContinue(string parentBatchId, Action<BatchContext> methodCall, string name);

        /// <summary>
        /// 重试失败的批量任务：只重新执行<b>未成功</b>的子任务，已成功的子任务保持原样、不会重复执行。
        /// 正在执行中的子任务同样跳过（避免重复投递），只有已失败或尚未执行的子任务会被重新展开。
        /// 批量状态回到 Init 并重新进入调度；已成功（<see cref="BatchStatus.Success"/>）、
        /// 尚未开始（<see cref="BatchStatus.None"/>）、等待父批量（<see cref="BatchStatus.Awaiting"/>）的批量不允许重试。
        /// </summary>
        /// <exception cref="Exceptions.BackendException">批量不存在，或当前状态不允许重试</exception>
        Task Retry(string batchId);
    }
}
