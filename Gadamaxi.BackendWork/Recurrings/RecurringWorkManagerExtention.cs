using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Recurrings
{
    public static class RecurringWorkManagerExtention
    {
        public static async Task AddOrUpdate(this IRecurringWorkManager manager,
            string recurringWorkId,
            Expression<Func<Task>> methodCall,
            string corn,
            string queue,
            RecurringWorkContext option,
            string name
            )
        {
            var work = Workload.FromExpression(methodCall, queue);
            await manager.AddOrUpdate(recurringWorkId, work, corn, option, name);
        }

        public static async Task AddOrUpdate<T>(this IRecurringWorkManager manager,
            string recurringWorkId,
            Expression<Func<T,Task>> methodCall,
            string corn,
            string queue,
            RecurringWorkContext option,
            string name
            )
        {
            var work = Workload.FromExpression(methodCall, typeof(T),queue);
            await manager.AddOrUpdate(recurringWorkId, work, corn, option, name);
        }

        public static async Task AddOrUpdate(this IRecurringWorkManager manager,
            string recurringWorkId,
            Expression<Action> methodCall,
            string corn,
            string queue,
            RecurringWorkContext option,
            string name
            )
        {
            var work = Workload.FromExpression(methodCall, queue);
            await manager.AddOrUpdate(recurringWorkId, work, corn, option, name);
        }

        public static async Task AddOrUpdate<T>(this IRecurringWorkManager manager,
            string recurringWorkId,
            Expression<Action<T>> methodCall,
            string corn,
            string queue,
            RecurringWorkContext option,
            string name
            )
        {
            var work = Workload.FromExpression(methodCall, typeof(T), queue);
            await manager.AddOrUpdate(recurringWorkId, work, corn, option, name);
        }
    }
}
