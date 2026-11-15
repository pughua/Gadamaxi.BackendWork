using Gadamaxi.BackendWork.Dashboards;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Gadamaxi.BackendWork.Mongodb
{
    public static class MongodbDashboardServiceCollectionExtensions
    {
        /// <summary>
        /// 注册 Mongodb 版 IDashboardRepository（<see cref="MongoDashboardRepository"/>）。
        /// 在 AddBackendWorkDashboard 之后调用；宿主已自行注册其他实现时可后续覆盖（DI 取最后注册者）。
        /// 要求宿主已通过 <c>UseMongodbBackendWork</c> 注册过 <see cref="MongoBackendWorkContext"/>。
        /// </summary>
        public static IServiceCollection AddMongodbDashboardRepository(this IServiceCollection services)
        {
            Helper.CheckArgumentNull(services, nameof(services));

            services.AddTransient<IDashboardRepository>(p => new MongoDashboardRepository(
                p.GetService<MongoBackendWorkContext>()
                    ?? throw new InvalidOperationException(
                        "注册 Mongodb 版面板仓储前，请先通过 UseMongodbBackendWork 注册存储后端。"),
                p.GetService<BackendWorkDashboardOptions>() ?? new BackendWorkDashboardOptions()));

            return services;
        }
    }
}
