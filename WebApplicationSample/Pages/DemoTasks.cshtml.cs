using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Works;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebApplicationSample.Pages
{
    /// <summary>
    /// 后台任务演示页：创建即时任务 / 延时任务 / 周期任务 / 批量任务。
    /// 所有任务负载落在 <see cref="SampleJobs"/>，执行结果展示在页面下方的日志区。
    /// </summary>
    public class DemoTasksModel : PageModel
    {
        // ---------- 即时任务 ----------
        [BindProperty] public string WorkName { get; set; } = "示例即时任务";
        [BindProperty] public string WorkMessage { get; set; } = "hello from sample page";

        // ---------- 延时任务 ----------
        [BindProperty] public string ScheduleName { get; set; } = "示例延时任务";
        [BindProperty] public string ScheduleMessage { get; set; } = "delayed hello";
        [BindProperty] public int ScheduleDelaySeconds { get; set; } = 15;

        // ---------- 周期任务 ----------
        [BindProperty] public string RecurringId { get; set; } = "sample-page-recurring";
        [BindProperty] public string RecurringName { get; set; } = "示例周期任务";
        [BindProperty] public string RecurringMessage { get; set; } = "recurring tick";
        [BindProperty] public string Cron { get; set; } = "* * * * *";

        // ---------- 批量任务 ----------
        [BindProperty] public string BatchName { get; set; } = "示例批量任务";
        [BindProperty] public int BatchCount { get; set; } = 20;

        public List<string> ExecLog => SampleJobs.Log.ToList();

        public string? Feedback { get; private set; }
        public string? Error { get; private set; }

        public void OnGet()
        {
            Feedback = TempData["Feedback"] as string;
            Error = TempData["Error"] as string;
        }

        /// <summary>创建即时任务：入队后由工作进程立即消费</summary>
        public async Task<IActionResult> OnPostEnqueueAsync()
        {
            try
            {
                var name = Normalize(WorkName, "示例即时任务");
                var message = Normalize(WorkMessage, "hello");
                var workId = await WorkHelper.Manager.Enqueue(() => SampleJobs.Hello(message), name);
                TempData["Feedback"] = $"即时任务已创建：WorkId = {workId}";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"创建即时任务失败：{ex.Message}";
            }
            return RedirectToPage();
        }

        /// <summary>创建延时任务：Schedule 指定延迟后进入队列</summary>
        public async Task<IActionResult> OnPostScheduleAsync()
        {
            try
            {
                var name = Normalize(ScheduleName, "示例延时任务");
                var message = Normalize(ScheduleMessage, "delayed hello");
                var delay = TimeSpan.FromSeconds(Math.Max(1, ScheduleDelaySeconds));
                var workId = await WorkHelper.Manager.Schedule(() => SampleJobs.Hello(message), delay, name);
                TempData["Feedback"] = $"延时任务已创建：WorkId = {workId}，{delay.TotalSeconds:0} 秒后执行";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"创建延时任务失败：{ex.Message}";
            }
            return RedirectToPage();
        }

        /// <summary>创建/更新周期任务：cron 表达式指定周期，重复提交同一 Id 即更新</summary>
        public async Task<IActionResult> OnPostRecurringAsync()
        {
            try
            {
                var id = Normalize(RecurringId, "sample-page-recurring");
                var name = Normalize(RecurringName, "示例周期任务");
                var message = Normalize(RecurringMessage, "recurring tick");
                var cron = Normalize(Cron, "* * * * *");

                await WorkHelper.Recurring.AddOrUpdate<SampleRecurringJob>(
                    id,
                    job => job.Tick(message),
                    cron,
                    "default",
                    new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Local },
                    name);
                TempData["Feedback"] = $"周期任务已保存：Id = {id}，周期 = {cron}（本地时区）";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"保存周期任务失败（请检查 cron 表达式）：{ex.Message}";
            }
            return RedirectToPage();
        }

        /// <summary>批量任务：一次性创建 N 个子任务</summary>
        public async Task<IActionResult> OnPostBatchAsync()
        {
            try
            {
                var name = Normalize(BatchName, "示例批量任务");
                var count = Math.Clamp(BatchCount, 1, 500);

                var batchId = await WorkHelper.Batch.Create(ctx =>
                {
                    for (var i = 1; i <= count; i++)
                    {
                        // 表达式树被序列化后重放：每个子任务用自己的局部变量，避免共享循环变量
                        var message = $"{name}#{i}";
                        ctx.Enqueue(() => SampleJobs.BatchItem(message));
                    }
                }, name);

                TempData["Feedback"] = $"批量任务已创建：BatchId = {batchId}，共 {count} 个子任务";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"创建批量任务失败：{ex.Message}";
            }
            return RedirectToPage();
        }

        private static string Normalize(string? value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
