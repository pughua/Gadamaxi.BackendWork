using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Recurrings
{
    public class RecurringWorkManager: IRecurringWorkManager
    {
        public RecurringWorkManager(
            IDistributedLock distributedLock,
            IWorkQueue workQueue,
            IWorkQuery workQuery)
        {
            _distributedLock = distributedLock;
            _workQuery = workQuery;
            _handlerProvider = DependencyInjectionHelper.Get<IWorkStateHandlerProvider>();
            _workQueue = workQueue;
            _option = DependencyInjectionHelper.Get<WorkServerOption>();
        }

        private readonly WorkServerOption _option;
        private readonly IDistributedLock _distributedLock;
        private readonly IWorkQuery _workQuery;
        private readonly IWorkQueue _workQueue;
        private readonly IWorkStateHandlerProvider _handlerProvider;

        public async Task AddOrUpdate(string recurringWorkId, Workload work, string cornExpression, RecurringWorkContext option, string name)
        {
            Helper.CheckArgumentNull(recurringWorkId, nameof(recurringWorkId));
            Helper.CheckArgumentNull(work, nameof(work));
            Helper.CheckArgumentNull(cornExpression, nameof(cornExpression));
            Helper.CheckArgumentNull(option,nameof(option));
            // 周期任务触发时以该名称作为工作名，因此与任务名同规则校验
            Helper.CheckWorkName(name);

            if (string.IsNullOrEmpty(work.Queue))
            {
                work.Queue = _option.Queues.First();
            }

            var cron = Helper.ParseCron(cornExpression);
            using(await _distributedLock.AcquireLock(recurringWorkId, TimeSpan.FromSeconds(15)))
            {
                var now = DateTime.UtcNow;
                await CreateOrUpdate(recurringWorkId, work, cornExpression, option,name);
            }
        }

        private async Task CreateOrUpdate(string recurringWorkId, Workload work, string cornExpression, RecurringWorkContext option,string name)
        {
            var recurringwork = await _workQuery.GetRecurringWorkById(recurringWorkId);
            using(var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                if (recurringwork == null)
                {
                    recurringwork = new RecurringWork(recurringWorkId);

                    recurringwork.Update(work, cornExpression, option, name);

                    recurringwork.GenNextExecuteTime(recurringwork.LastExecuteUtcTime ?? recurringwork.CreateUtcTime.AddSeconds(-1),
                        recurringwork.CreateUtcTime);

                    await storage.AddRecurringWork(recurringwork);

                    var nextStamp = recurringwork.NextExecuteUtcTime!.Value.UnixMillisecondStamp();
                    await storage.AddCacheSet(Helper.RecurringKey,
                        nextStamp,
                        recurringWorkId);
                }
                else
                {
                    recurringwork.Update(work, cornExpression, option, name);
                    await storage.UpdateRecurringWork(recurringwork);
                }

                await storage.Commit();
            }
        }

        public async Task Trigger(string id)
        {
            Helper.CheckArgumentNull(id, nameof(id));
            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                await WorkFactory.TriggerRecurringWork(storage!, _workQuery, _workQueue, _handlerProvider, id, this.GetType().Name);
                try
                {
                    await storage!.Commit();
                }
                catch (RecurringDuplicateTriggerException)
                {
                    // CAS 抢占失败：该次触发已被其它触发者处理，属正常并发去重
                }
            }
        }

        public async Task RemoveIfExists(string recurringJobId)
        {
            Helper.CheckArgumentNull(recurringJobId, nameof(recurringJobId));
            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                await storage.RemoveRecurringWork(recurringJobId);
                await storage.RemoveCacheSet(Helper.RecurringKey, recurringJobId);
                // 存储是事务性的，删除必须提交，否则回滚后周期任务仍然存在
                await storage.Commit();
            }
        }
    }
}
