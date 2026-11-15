using System;

namespace Gadamaxi.BackendWork.Exceptions
{
    /// <summary>
    /// 周期任务触发被跳过：CAS 抢占失败，说明该次触发已被其它触发者（并发的调度触发 / 手动触发）处理。
    /// 由 <see cref="Storages.IWorkStorage.AdvanceRecurringWork"/> 在抢占失败时抛出，
    /// 用于中止并回滚当前触发事务，从而不产生重复的工作（#11 防重）。
    /// </summary>
    public class RecurringDuplicateTriggerException : BackendException
    {
        public RecurringDuplicateTriggerException(string recurringId)
            : base($"RecurringWork {recurringId} trigger was already claimed by another trigger; skip duplicate.")
        {
            RecurringId = recurringId;
        }

        /// <summary>发生重复触发的周期任务 Id。</summary>
        public string RecurringId { get; }
    }
}
