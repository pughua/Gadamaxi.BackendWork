using Gadamaxi.BackendWork.Dashboards;
using Microsoft.Extensions.DependencyInjection;

namespace Gadamaxi.BackendWork.Sql
{
    public static class SqlDashboardServiceCollectionExtensions
    {
        /// <summary>
        /// 注册关系库（linq2db）版 IDashboardRepository。在 AddBackendWorkDashboard 之后调用；
        /// 若宿主已自行注册其他实现，可在其后调用本方法覆盖（DI 取最后注册者）。
        /// </summary>
        public static IServiceCollection AddSqlDashboardRepository(this IServiceCollection services)
        {
            services.AddTransient<IDashboardRepository, SqlDashboardRepository>();
            return services;
        }
    }
}
