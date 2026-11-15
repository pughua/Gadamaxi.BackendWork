using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Dashboards;
using Gadamaxi.BackendWork.Dashboard.Storage;
using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Works;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace Gadamaxi.BackendWork.Dashboard.Storage
{
    /// <summary>
    /// 面板写操作编排。数据访问全部委托 IDashboardRepository（仓储实现在具体存储包中），
    /// 业务动作走框架管理器（IWorkManager/IRecurringWorkManager），本质是 DB 级状态变更，
    /// 各节点本就轮询 DB 领取工作，无需节点间指令广播。
    /// <para>
    /// 每次写操作输出一条审计日志（操作者取宿主认证身份）。审计<b>不落库</b>：
    /// 框架不为此新增任何表，宿主如需持久化，把日志采集到自己的审计系统即可。
    /// </para>
    /// 已知限制：运行中（processing）任务的取消暂不支持——框架尚无协作取消信号，返回 409；
    /// 批量重试只针对<b>失败</b>（Failure）的批量，重跑其中未成功的子任务，已成功的不会重复执行。
    /// </summary>
    public class DashboardCommands
    {
        private readonly IDashboardRepository _repository;
        private readonly IWorkManager _workManager;
        private readonly IRecurringWorkManager _recurringManager;
        private readonly IBatchManager _batchManager;
        private readonly ILogger<DashboardCommands> _logger;

        public DashboardCommands(IDashboardRepository repository, IWorkManager workManager,
            IRecurringWorkManager recurringManager, IBatchManager batchManager,
            ILogger<DashboardCommands> logger)
        {
            _repository = repository;
            _workManager = workManager;
            _recurringManager = recurringManager;
            _batchManager = batchManager;
            _logger = logger;
        }

        public record CommandResult(bool Succeeded, string Code, string Message)
        {
            public static CommandResult Ok(string message = "accepted") => new(true, "OK", message);
            public static CommandResult Fail(string code, string message) => new(false, code, message);
        }

        // ---------- 后台任务 ----------

        public async Task<CommandResult> RetryWork(string workId, ClaimsPrincipal user)
        {
            var operatorName = GetOperator(user);
            var result = await Run("retry", "work", workId, operatorName, async () =>
            {
                var state = await _repository.GetWorkStateAsync(workId);
                if (state == null) return CommandResult.Fail("TASK_NOT_FOUND", "任务不存在");
                if (state != "failed") return CommandResult.Fail("TASK_NOT_RETRYABLE", $"仅失败任务可重试，当前状态：{state}");
                await _workManager.ReEnqueue(workId);
                return CommandResult.Ok("已重新入队");
            });
            return result;
        }

        public async Task<CommandResult> CancelWork(string workId, ClaimsPrincipal user)
        {
            var operatorName = GetOperator(user);
            return await Run("cancel", "work", workId, operatorName, async () =>
            {
                var state = await _repository.GetWorkStateAsync(workId);
                if (state == null) return CommandResult.Fail("TASK_NOT_FOUND", "任务不存在");
                if (state == "processing") return CommandResult.Fail("PROCESSING_NOT_CANCELLABLE", "运行中任务暂不支持取消（框架尚无协作取消信号）");
                if (state is not ("scheduled" or "enqueued" or "awaiting"))
                    return CommandResult.Fail("TASK_NOT_CANCELLABLE", $"仅排队中任务可取消，当前状态：{state}");
                await _workManager.RemoveWork(workId);
                return CommandResult.Ok("已取消");
            });
        }

        // ---------- 批量任务 ----------

        public async Task<CommandResult> RetryBatch(string batchId, ClaimsPrincipal user)
        {
            var operatorName = GetOperator(user);
            return await Run("retry", "batch", batchId, operatorName, async () =>
            {
                var detail = _repository.GetBatchDetail(batchId);
                if (detail == null) return CommandResult.Fail("BATCH_NOT_FOUND", "批量任务不存在");
                if (!string.Equals(detail.Batch.Status, nameof(BatchStatus.Failure), StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail("BATCH_NOT_RETRYABLE", $"仅失败批量可重试，当前状态：{detail.Batch.Status}");
                try
                {
                    await _batchManager.Retry(batchId);
                }
                catch (BackendException ex)
                {
                    return CommandResult.Fail("BATCH_NOT_RETRYABLE", ex.Message);
                }
                return CommandResult.Ok("已重新展开未成功的子任务（已成功的不会重复执行）");
            });
        }

        // ---------- 周期任务 ----------

        public async Task<CommandResult> TriggerRecurring(string recurringId, ClaimsPrincipal user)
        {
            var operatorName = GetOperator(user);
            return await Run("trigger", "recurring", recurringId, operatorName, async () =>
            {
                var exists = await _repository.RecurringExistsAsync(recurringId);
                if (!exists) return CommandResult.Fail("RECURRING_NOT_FOUND", "周期任务不存在");
                try
                {
                    await _recurringManager.Trigger(recurringId);
                }
                catch (RecurringDuplicateTriggerException)
                {
                    // CAS 抢占失败 = 本次触发已被其它触发者处理，属正常并发去重
                }
                return CommandResult.Ok("已触发");
            });
        }

        public async Task<CommandResult> SetRecurringPaused(string recurringId, bool paused, ClaimsPrincipal user)
        {
            var action = paused ? "pause" : "resume";
            var operatorName = GetOperator(user);
            return await Run(action, "recurring", recurringId, operatorName, async () =>
            {
                var rows = await _repository.SetRecurringPausedAsync(recurringId, paused);
                if (rows == 0) return CommandResult.Fail("RECURRING_NOT_FOUND", "周期任务不存在");
                return CommandResult.Ok(paused ? "已暂停" : "已恢复");
            });
        }

        // ---------- 内部 ----------

        private async Task<CommandResult> Run(string action, string targetType, string targetId,
            string operatorName, Func<Task<CommandResult>> body)
        {
            CommandResult result;
            try
            {
                result = await body();
            }
            catch (Exception ex)
            {
                result = CommandResult.Fail("INTERNAL_ERROR", ex.Message);
            }

            // 审计以日志输出（不落库）：宿主可通过日志采集接入自己的审计系统
            if (result.Succeeded)
            {
                _logger.LogInformation(
                    "Dashboard write op: operator={Operator}, action={Action}, target={TargetType}/{TargetId}, result={Message}",
                    operatorName, action, targetType, targetId, result.Message);
            }
            else
            {
                _logger.LogWarning(
                    "Dashboard write op failed: operator={Operator}, action={Action}, target={TargetType}/{TargetId}, code={Code}, reason={Message}",
                    operatorName, action, targetType, targetId, result.Code, result.Message);
            }

            return result;
        }

        private static string GetOperator(ClaimsPrincipal? user)
        {
            var name = user?.Identity?.Name;
            if (!string.IsNullOrEmpty(name)) return name;
            var claim = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user?.FindFirst("sub")?.Value;
            return string.IsNullOrEmpty(claim) ? "anonymous" : claim;
        }
    }
}
