using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Sagas;
using Gadamaxi.BackendWork.Works;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace Gadamaxi.BackendWork
{
    public static class WorkHelper
    {
        public static IWorkManager Manager => DependencyInjectionHelper.Get<IWorkManager>();

        public static IRecurringWorkManager Recurring => DependencyInjectionHelper.Get<IRecurringWorkManager>();
        public static IBatchManager Batch => DependencyInjectionHelper.Get<IBatchManager>();

        /// <summary>saga（编排式分布式事务）的创建与管理入口</summary>
        public static ISagaManager Saga => DependencyInjectionHelper.Get<ISagaManager>();
    }
}
