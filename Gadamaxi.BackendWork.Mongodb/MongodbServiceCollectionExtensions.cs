using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;

namespace Gadamaxi.BackendWork.Mongodb
{
    public static class MongodbServiceCollectionExtensions
    {
        /// <summary>
        /// 用 Mongodb 作为后台任务的存储实现。
        /// <para>
        /// Mongodb 没有"迁移"概念（集合在首次写入时自动创建），这里替代
        /// <c>UseSqlBackendWork</c> 中 <c>MigrateUp()</c> 角色的是<b>索引幂等创建</b>
        /// （<see cref="MongoBackendWorkContext.EnsureIndexesAsync"/>），它在上下文首次解析时执行一次。
        /// 宿主若想在启动阶段就完成建索引与连通性校验，可在装配后手动调用
        /// <see cref="MongoBackendWorkContext.DetectTransactionsAsync"/> 与
        /// <see cref="MongoBackendWorkContext.EnsureIndexesAsync"/>。
        /// </para>
        /// <para>
        /// 注意：多文档事务需要<b>副本集</b>。单机部署会在探测时记警告并退化为逐条写入；
        /// 把 <see cref="MongoBackendWorkOptions.RequireTransactions"/> 置为 true 可让这种情况直接抛异常。
        /// </para>
        /// </summary>
        public static IConfig UseMongodbBackendWork(this IConfig config, IServiceCollection services,
            Func<MongoBackendWorkOptions> setOption, bool ensureIndexes = true)
        {
            Helper.CheckArgumentNull(config, nameof(config));
            Helper.CheckArgumentNull(services, nameof(services));
            Helper.CheckArgumentNull(setOption, nameof(setOption));

            var options = setOption();

            // 上下文由容器工厂创建：ILoggerFactory 从真正的服务提供程序取，
            // 不为拿 logger 去二次 BuildServiceProvider（那会造出第二套单例）。
            services.AddSingleton(p =>
            {
                var context = new MongoBackendWorkContext(options, p.GetService<ILoggerFactory>());

                if (ensureIndexes)
                {
                    // 单例工厂只执行一次 ⇒ 探测与建索引天然只做一次；失败直接暴露（连不上库就该起不来）
                    context.DetectTransactionsAsync().GetAwaiter().GetResult();
                    context.EnsureIndexesAsync().GetAwaiter().GetResult();
                }

                return context;
            });

            config.WorkStorageFunc = p => new WorkStorage(p.GetService<MongoBackendWorkContext>());

            config.WorkQueryFunc = p => new WorkQuery(p.GetService<MongoBackendWorkContext>());

            config.WorkQueueFunc = p => new WorkQueue(p.GetService<MongoBackendWorkContext>(),
                p.GetService<WorkServerOption>() ?? new WorkServerOption(),
                p.GetService<ILogger<WorkQueue>>());

            config.DistributedLockFunc = p => new DistributedLock(p.GetService<MongoBackendWorkContext>());

            return config;
        }
    }
}
