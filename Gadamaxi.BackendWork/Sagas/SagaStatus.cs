using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// saga 实例的整体阶段。正向只走 Running，失败（或超时/取消）后转入 Compensating，
    /// 三条终态路径分别落到 Completed / Compensated / Aborted。
    /// </summary>
    public enum SagaStatus
    {
        /// <summary>已登记，等待首次调度</summary>
        None = 0,

        /// <summary>正向推进中</summary>
        Running = 1,

        /// <summary>逆序补偿中</summary>
        Compensating = 2,

        /// <summary>正向全部成功（终态）</summary>
        Completed = 3,

        /// <summary>补偿全部完成，业务上等价于"未发生"（终态）</summary>
        Compensated = 4,

        /// <summary>补偿失败且无法自动继续，需人工介入（终态）</summary>
        Aborted = 5
    }
}
