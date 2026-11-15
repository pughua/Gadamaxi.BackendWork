using Gadamaxi.BackendWork.Dashboard.Storage;
using Gadamaxi.BackendWork.Dashboards;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;

namespace Gadamaxi.BackendWork.Dashboard
{
    /// <summary>
    /// 面板 HTTP API。统一响应：成功 { data, meta? }，失败 { error: { code, message } }。
    /// 读端点共用读授权策略；写端点默认不注册（EnableWriteOperations），需独立 WritePolicy 且受 ReadOnly 硬开关约束。
    /// </summary>
    public static class DashboardApi
    {
        public static void Map(IEndpointRouteBuilder endpoints, BackendWorkDashboardOptions options)
        {
            var api = endpoints.MapGroup(options.NormalizedPrefix + "/api");

            // ---------- 读端点 ----------
            var read = string.IsNullOrEmpty(options.AuthorizationPolicy)
                ? api.RequireAuthorization()
                : api.RequireAuthorization(new AuthorizeAttribute { Policy = options.AuthorizationPolicy });

            read.MapGet("/overview", (IDashboardRepository queries) =>
                Results.Ok(new { data = queries.GetOverview() }));

            read.MapGet("/tasks", (IDashboardRepository queries, string? state, string? name, string? queue,
                string? id, int page = 1, int pageSize = 20) =>
            {
                var result = queries.QueryTasks(state, name, queue, id, page, pageSize);
                return Results.Ok(new { data = result.Items, meta = new { total = result.Total, page, pageSize } });
            });

            read.MapGet("/tasks/{id}", (IDashboardRepository queries, string id) =>
            {
                var detail = queries.GetTaskDetail(id);
                return detail == null
                    ? NotFound("TASK_NOT_FOUND", "任务不存在")
                    : Results.Ok(new { data = detail });
            });

            read.MapGet("/concurrency", (IDashboardRepository queries) =>
                Results.Ok(new { data = queries.GetConcurrency() }));

            read.MapGet("/recurring", (IDashboardRepository queries, int page = 1, int pageSize = 20) =>
            {
                var result = queries.QueryRecurrings(page, pageSize);
                return Results.Ok(new { data = result.Items, meta = new { total = result.Total, page, pageSize } });
            });

            read.MapGet("/recurring/{id}/history", (IDashboardRepository queries, string id,
                int page = 1, int pageSize = 20) =>
            {
                var result = queries.GetRecurringHistory(id, page, pageSize);
                return Results.Ok(new { data = result.Items, meta = new { total = result.Total, page, pageSize } });
            });

            read.MapGet("/batches", (IDashboardRepository queries, string? status, string? name,
                int page = 1, int pageSize = 20) =>
            {
                var result = queries.QueryBatches(status, name, page, pageSize);
                return Results.Ok(new { data = result.Items, meta = new { total = result.Total, page, pageSize } });
            });

            read.MapGet("/batches/{id}", (IDashboardRepository queries, string id) =>
            {
                var detail = queries.GetBatchDetail(id);
                return detail == null
                    ? NotFound("BATCH_NOT_FOUND", "批量任务不存在")
                    : Results.Ok(new { data = detail });
            });

            read.MapGet("/sagas", (IDashboardRepository queries, string? status, bool? stalled, int page = 1, int pageSize = 20) =>
            {
                var result = queries.QuerySagas(status, stalled, page, pageSize);
                return Results.Ok(new { data = result.Items, meta = new { total = result.Total, page, pageSize } });
            });

            read.MapGet("/sagas/{id}", (IDashboardRepository queries, string id) =>
            {
                var detail = queries.GetSagaDetail(id);
                return detail == null
                    ? NotFound("SAGA_NOT_FOUND", "Saga 不存在")
                    : Results.Ok(new { data = detail });
            });

            read.MapGet("/nodes", (IDashboardRepository queries) =>
                Results.Ok(new { data = queries.GetNodes() }));

            read.MapGet("/nodes/{name}", (IDashboardRepository queries, string name) =>
            {
                var node = queries.GetNode(name);
                return node == null
                    ? NotFound("NODE_NOT_FOUND", "节点不存在")
                    : Results.Ok(new { data = node });
            });

            // ---------- 写端点（默认不注册） ----------
            if (!options.EnableWriteOperations) return;

            if (string.IsNullOrEmpty(options.WritePolicy))
                throw new InvalidOperationException(
                    "EnableWriteOperations=true 时必须配置 WritePolicy（写操作独立授权策略）。");

            var write = api.MapGroup(string.Empty)
                .RequireAuthorization(new AuthorizeAttribute { Policy = options.WritePolicy });

            write.MapPost("/tasks/{id}/retry", async (string id, DashboardCommands commands, ClaimsPrincipal user) =>
                await RunWrite(options, () => commands.RetryWork(id, user)));

            write.MapPost("/tasks/{id}/cancel", async (string id, DashboardCommands commands, ClaimsPrincipal user) =>
                await RunWrite(options, () => commands.CancelWork(id, user)));

            write.MapPost("/batches/{id}/retry", async (string id, DashboardCommands commands, ClaimsPrincipal user) =>
                await RunWrite(options, () => commands.RetryBatch(id, user)));

            write.MapPost("/recurring/{id}/trigger", async (string id, DashboardCommands commands, ClaimsPrincipal user) =>
                await RunWrite(options, () => commands.TriggerRecurring(id, user)));

            write.MapPost("/recurring/{id}/pause", async (string id, DashboardCommands commands, ClaimsPrincipal user) =>
                await RunWrite(options, () => commands.SetRecurringPaused(id, true, user)));

            write.MapPost("/recurring/{id}/resume", async (string id, DashboardCommands commands, ClaimsPrincipal user) =>
                await RunWrite(options, () => commands.SetRecurringPaused(id, false, user)));
        }

        private static async Task<IResult> RunWrite(BackendWorkDashboardOptions options,
            Func<Task<DashboardCommands.CommandResult>> action)
        {
            if (options.ReadOnly)
            {
                return Error(403, "READONLY_MODE", "面板处于全局只读模式（ReadOnly=true），拒绝一切写操作。");
            }

            var result = await action();
            if (result.Succeeded)
            {
                return Results.Ok(new { data = new { accepted = true, message = result.Message } });
            }

            var status = result.Code.EndsWith("_NOT_FOUND", StringComparison.Ordinal) ? 404 : 409;
            return Error(status, result.Code, result.Message);
        }

        private static IResult NotFound(string code, string message) => Error(404, code, message);

        private static IResult Error(int status, string code, string message) =>
            Results.Json(new { error = new { code, message } }, statusCode: status);
    }
}
