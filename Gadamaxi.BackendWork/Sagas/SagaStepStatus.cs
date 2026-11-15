using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// 单个 saga 步骤的状态。一个步骤行同时承载正向与补偿两个负载，
    /// 用本状态表达它当前处于哪条路径、走到了哪一步。
    /// </summary>
    public enum SagaStepStatus
    {
        /// <summary>未展开</summary>
        Init = 0,

        /// <summary>正向工作已展开，在执行链路中</summary>
        Running = 1,

        /// <summary>正向成功</summary>
        Succeeded = 2,

        /// <summary>正向终态失败（重试已耗尽）</summary>
        Failed = 3,

        /// <summary>本步骤无需动作（流程中止未执行 / 该步骤不可补偿）</summary>
        Skipped = 4,

        /// <summary>补偿工作已展开</summary>
        Compensating = 5,

        /// <summary>补偿成功</summary>
        Compensated = 6,

        /// <summary>补偿终态失败</summary>
        CompensationFailed = 7
    }
}
