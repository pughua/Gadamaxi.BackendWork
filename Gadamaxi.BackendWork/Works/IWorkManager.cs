using Gadamaxi.BackendWork.Works.States;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works
{
    public interface IWorkManager
    {
        /// <summary>
        /// 创建工作。name 为人工可识别的任务名（监控面板使用），必传。
        /// </summary>
        Task<string> Create(CreateWorkContext context, string name);

        Task<bool> ChangeState(WorkStateContext context);

        Task RemoveWork(string workId);

        Task ReEnqueue(string workId);
    }
}
