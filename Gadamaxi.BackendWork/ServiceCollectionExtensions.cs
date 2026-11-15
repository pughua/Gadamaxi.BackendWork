using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Sagas;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Gadamaxi.BackendWork
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddBackendWork(this IServiceCollection services,
            WorkServerOption option
            , Action<IConfig> config)
        {
            Helper.CheckArgumentNull(option, nameof(option));

            var cfg = new Config();

            config(cfg);

            services.AddTransient(p =>
            {
                return cfg.DistributedLockFunc(p);
            });

            services.AddTransient(p =>
            {
                return cfg.WorkQueryFunc(p);
            });

            services.AddTransient(p =>
            {
                return cfg.WorkQueueFunc(p);
            });

            services.AddTransient(p =>
            {
                return cfg.WorkStorageFunc(p);
            });

            services.AddSingleton<IConfig>(provider =>
            {
                DependencyInjectionHelper.Set(provider);
                return cfg;
            });

            // Options Pattern（§2.4-4）：配置统一经 IOptions<WorkServerOption> 取值，
            // 避免各处直接持有可变 POCO 单例。这里仍按原实例包装，保证运行期调整配置即时生效（测试亦然）。
            services.AddOptions();
            services.AddSingleton<IOptions<WorkServerOption>>(p => new OptionsWrapper<WorkServerOption>(option));
            services.AddSingleton((p) => option);

            // WorkServer 的协作组件改为可注入（#15），不再在构造函数里硬编码 new
            services.AddTransient<IWorkExecution, WorkExecution>();
            services.AddTransient<IWorkDispatcher, WorkDispatcher>();
            services.AddTransient<IWorkServerProcess, WorkServerProcess>();
            services.AddTransient<IWorkServer, WorkServer>();
            services.AddSingleton<IWorkStateHandlerProvider, WorkStateHandlerProvider>();
            services.AddSingleton<ICreatePipelineProvider, CreatePipelineProvider>();

            services.AddTransient<IWorkManager, WorkManager>();
            services.AddTransient<IRecurringWorkManager, RecurringWorkManager>();

            services.AddSingleton<IBatchStatusPipelineProvider, BatchStatusPipelineProvider>();
            services.AddTransient<IBatchManager, BatchManager>();
            services.AddTransient<IChangeStatePipeline, BatchChangeWorkStatePipeline>();
            services.AddTransient<IWorkProcess, BatchWorkProcess>();

            // Saga：编排式分布式事务。ISagaManager 负责创建/查询/人工干预，
            // SagaWorkProcess 负责游标推进，SagaTimeoutProcess 负责整体超时转补偿，
            // SagaChangeWorkStatePipeline 把步骤工作的终态回写到 SagaStep 并唤醒调度缓存。
            services.AddTransient<ISagaManager, SagaManager>();
            services.AddTransient<IChangeStatePipeline, SagaChangeWorkStatePipeline>();
            services.AddTransient<IWorkProcess, SagaWorkProcess>();
            services.AddTransient<IWorkProcess, SagaTimeoutProcess>();

            if (option.UseBackendServer)
            {
                services.AddTransient<IHostedService>((p) =>
                {
                    CheckConfigured(p);
                    var server = p.GetService<IWorkServer>();
                    return new ServerBackgroundService(server!);
                });
            }
            return services;
        }

        private static void CheckConfigured(IServiceProvider serviceProvider)
        {
            var config = serviceProvider.GetService<IConfig>();
            if (config == null)
            {
                throw new ArgumentNullException(nameof(IConfig));
            }
        }
    }
}
