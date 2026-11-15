using Gadamaxi.BackendWork.Dashboard.Storage;
using Gadamaxi.BackendWork.Dashboards;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Gadamaxi.BackendWork.Dashboard
{
    public static class BackendWorkDashboardExtensions
    {
        /// <summary>
        /// 注册面板服务（Options、查询、命令）。在宿主 Program.cs 的 AddBackendWork 之后调用。
        /// </summary>
        public static IServiceCollection AddBackendWorkDashboard(this IServiceCollection services,
            Action<BackendWorkDashboardOptions>? configure = null)
        {
            var options = new BackendWorkDashboardOptions();
            configure?.Invoke(options);
            services.AddSingleton(options);
            services.AddSingleton<IOptions<BackendWorkDashboardOptions>>(new Microsoft.Extensions.Options.OptionsWrapper<BackendWorkDashboardOptions>(options));
            services.AddTransient<DashboardCommands>();
            return services;
        }

        /// <summary>
        /// 挂载监控面板：嵌入式前端页面 + HTTP API。
        /// 页面在 {RoutePrefix}，API 在 {RoutePrefix}/api。需在 UseAuthentication/UseAuthorization 之后调用。
        /// </summary>
        public static IEndpointRouteBuilder MapBackendWorkDashboard(this IEndpointRouteBuilder endpoints)
        {
            var options = endpoints.ServiceProvider.GetRequiredService<IOptions<BackendWorkDashboardOptions>>().Value;

            // 仓储实现由存储包（或宿主）注册：关系库用 AddSqlDashboardRepository()，其他数据库自行实现 IDashboardRepository
            if (endpoints.ServiceProvider.GetService<IDashboardRepository>() == null)
            {
                throw new InvalidOperationException(
                    "未注册 Gadamaxi.BackendWork.Dashboards.IDashboardRepository 实现。" +
                    "请在宿主中调用 AddSqlDashboardRepository()（关系库实现，位于 Gadamaxi.BackendWork.Sql 包），" +
                    "或自行注册其他存储的实现。");
            }

            // 失联阈值自动校准：框架默认心跳间隔为 5 分钟，若面板阈值小于 2×心跳间隔，
            // 节点会在两次心跳之间被误判为失联。取 max(配置值, 2×心跳间隔)。
            var serverOption = endpoints.ServiceProvider.GetService<Gadamaxi.BackendWork.Server.WorkServerOption>()
                ?? endpoints.ServiceProvider.GetService<IOptions<Gadamaxi.BackendWork.Server.WorkServerOption>>()?.Value;
            if (serverOption != null)
            {
                var minThreshold = (int)serverOption.HeartbeatDelay.TotalSeconds * 2;
                if (options.NodeOfflineThresholdSeconds < minThreshold)
                    options.NodeOfflineThresholdSeconds = minThreshold;
            }

            DashboardApi.Map(endpoints, options);
            MapDashboardUi(endpoints, options);
            return endpoints;
        }

        /// <summary>
        /// 以嵌入式资源服务前端页面：{prefix} 与无扩展名路径回退到 index.html（SPA），
        /// 带哈希指纹的静态资源长缓存。资源内嵌于程序集，不依赖宿主 wwwroot / UseStaticFiles。
        /// </summary>
        private static void MapDashboardUi(IEndpointRouteBuilder endpoints, BackendWorkDashboardOptions options)
        {
            var prefix = options.NormalizedPrefix;

            var endpoint = endpoints.MapGet(prefix + "/{*path}", async (HttpContext ctx, string? path) =>
            {
                await ServeAsync(ctx, path, options);
            });

            // 页面与 API 共用读授权策略：可配置为宿主自定义策略名（角色/Claim 要求由宿主定义），
            // 未配置时要求"已认证用户"
            if (string.IsNullOrEmpty(options.AuthorizationPolicy))
                endpoint.RequireAuthorization();
            else
                endpoint.RequireAuthorization(new AuthorizeAttribute { Policy = options.AuthorizationPolicy });
        }

        private static async Task ServeAsync(HttpContext ctx, string? path, BackendWorkDashboardOptions options)
        {
            var assembly = typeof(BackendWorkDashboardExtensions).Assembly;
            var root = assembly.GetName().Name + ".wwwroot";
            path = (path ?? string.Empty).TrimStart('/');
            var prefix = options.NormalizedPrefix;

            // 挂载点必须带尾斜杠访问：前端产物使用相对路径引用资源（./assets/...），
            // 若以 {prefix}（无尾斜杠）打开，浏览器会把资源解析到站点根导致 404
            if (string.IsNullOrEmpty(path) && !ctx.Request.Path.Value!.EndsWith('/'))
            {
                ctx.Response.Redirect(prefix + "/", permanent: true);
                return;
            }

            // 防目录穿越：拒绝包含 .. 的路径
            if (path.Contains("..", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            var isIndex = string.IsNullOrEmpty(path) || path == "index.html" ||
                          (!path.Contains('.', StringComparison.Ordinal)); // SPA 路由回退
            var resourcePath = isIndex ? "index.html" : path;
            var full = $"{root}.{resourcePath.Replace('/', '.')}";

            if (isIndex && resourcePath != "index.html") full = $"{root}.index.html";

            var stream = assembly.GetManifestResourceStream(full);
            if (stream == null)
            {
                // 未命中资源：有扩展名视为真实 404；否则回退 index.html（前端路由）
                if (resourcePath.Contains('.', StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }
                stream = assembly.GetManifestResourceStream($"{root}.index.html");
                if (stream == null)
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsync("Dashboard frontend assets are not embedded. See dashboard-ui/README.md to build them.");
                    return;
                }
                resourcePath = "index.html";
            }

            using (stream)
            {
                ctx.Response.ContentType = GetContentType(resourcePath);
                if (resourcePath == "index.html")
                {
                    ctx.Response.Headers.CacheControl = "no-cache";
                    // 把构建产物的相对资源引用（./assets/...）改写为挂载前缀下的绝对路径，
                    // 使面板无论以何种路径形态打开资源都不会 404
                    using var reader = new StreamReader(stream);
                    var html = (await reader.ReadToEndAsync()).Replace("\"./", $"\"{prefix}/");
                    await ctx.Response.WriteAsync(html);
                }
                else
                {
                    // 构建产物带内容哈希指纹，可长缓存
                    ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                    await stream.CopyToAsync(ctx.Response.Body);
                }
            }
        }

        private static string GetContentType(string path)
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".html" => "text/html; charset=utf-8",
                ".htm" => "text/html; charset=utf-8",
                ".js" => "application/javascript; charset=utf-8",
                ".mjs" => "application/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".ico" => "image/x-icon",
                ".woff" => "font/woff",
                ".woff2" => "font/woff2",
                ".ttf" => "font/ttf",
                ".map" => "application/json",
                ".txt" => "text/plain; charset=utf-8",
                _ => "application/octet-stream"
            };
        }
    }
}
