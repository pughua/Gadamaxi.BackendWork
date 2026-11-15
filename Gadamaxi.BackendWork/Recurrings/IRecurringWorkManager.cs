using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Recurrings
{
    public interface IRecurringWorkManager
    {
        /// <summary>
        /// 新增或更新周期任务。name 为人工可识别的名称，必传：
        /// 周期任务触发时创建的工作会继承该名称作为任务名。
        /// </summary>
        Task AddOrUpdate(string recurringWorkId,Workload work,string cornExpression, RecurringWorkContext option, string name);
        Task Trigger(string id);

        Task RemoveIfExists(string recurringJobId);
    }
}
