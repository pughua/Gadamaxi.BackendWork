using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Sagas;
using Gadamaxi.BackendWork.Works;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebApplicationTest.Pages
{
    /// <summary>
    /// 后台任务演示页：创建即时任务 / 延时任务 / 周期任务（可指定周期）/ saga 事务测试。
    /// </summary>
    public class DemoTasksModel : PageModel
    {
        // ---------- 即时任务 ----------
        [BindProperty] public string WorkName { get; set; } = "页面演示任务";
        [BindProperty] public string WorkMessage { get; set; } = "hello from razor page";

        // ---------- 延时任务 ----------
        [BindProperty] public string ScheduleName { get; set; } = "页面演示延时任务";
        [BindProperty] public string ScheduleMessage { get; set; } = "delayed hello";
        [BindProperty] public int ScheduleDelaySeconds { get; set; } = 30;

        // ---------- 周期任务 ----------
        [BindProperty] public string RecurringId { get; set; } = "page-demo-recurring";
        [BindProperty] public string RecurringName { get; set; } = "页面演示周期任务";
        [BindProperty] public string RecurringMessage { get; set; } = "recurring tick";
        [BindProperty] public string Cron { get; set; } = "* * * * *";

        // ---------- 批量任务 ----------
        [BindProperty] public string BatchName { get; set; } = "页面批量任务测试";
        [BindProperty] public int BatchCount { get; set; } = 20;
        [BindProperty] public int BatchDelaySeconds { get; set; } = 0;
        [BindProperty] public int BatchFailIndex { get; set; } = 0;

        // ---------- Awaiting（连续任务）测试 ----------
        [BindProperty] public string AwaitingName { get; set; } = "页面Awaiting连续任务测试";
        [BindProperty] public int AwaitingParentSeconds { get; set; } = 30;
        [BindProperty] public int AwaitingChildCount { get; set; } = 3;

        // ---------- 并发执行测试数据 ----------
        [BindProperty] public int ConcurrencyCount { get; set; } = 12;
        [BindProperty] public int ConcurrencySeconds { get; set; } = 90;
        [BindProperty] public bool ContinuousFeed { get; set; }

        /// <summary>持续供给的固定周期任务 Id（重复开启 = 更新，停止 = 移除）</summary>
        private const string FeedRecurringId = "demo-concurrency-feed";

        // ---------- Saga 测试 ----------
        [BindProperty] public string SagaName { get; set; } = "页面Saga事务测试";
        [BindProperty] public string CorrelationId { get; set; } = "";
        [BindProperty] public int FailStep { get; set; } = 0;

        public List<string> ExecLog => DemoJobs.Log.ToList();

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
            var name = Normalize(WorkName, "页面演示任务");
            try
            {
                var message = Normalize(WorkMessage, "hello");
                var workId = await WorkHelper.Manager.Enqueue(() => DemoJobs.Hello(message), name);
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
                var name = Normalize(ScheduleName, "页面演示延时任务");
                var message = Normalize(ScheduleMessage, "delayed hello");
                var delay = TimeSpan.FromSeconds(Math.Max(1, ScheduleDelaySeconds));
                var workId = await WorkHelper.Manager.Schedule(() => DemoJobs.Hello(message), delay, name);
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
                var id = Normalize(RecurringId, "page-demo-recurring");
                var name = Normalize(RecurringName, "页面演示周期任务");
                var message = Normalize(RecurringMessage, "recurring tick");
                var cron = Normalize(Cron, "* * * * *");

                await WorkHelper.Recurring.AddOrUpdate<DemoRecurringJob>(
                    id,
                    job => job.RecurringTick(message),
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

        /// <summary>
        /// 批量任务：一次性创建 N 个子任务；可选每个子任务延时入队，或指定某个子任务故意失败
        /// （失败后用面板/Recurring 视图观察；批量重试只重跑未成功的子任务）。
        /// </summary>
        public async Task<IActionResult> OnPostBatchAsync()
        {
            try
            {
                var name = Normalize(BatchName, "页面批量任务测试");
                var count = Math.Clamp(BatchCount, 1, 500);
                var delay = TimeSpan.FromSeconds(Math.Max(0, BatchDelaySeconds));
                var failIndex = BatchFailIndex;

                var batchId = await WorkHelper.Batch.Create(ctx =>
                {
                    for (var i = 1; i <= count; i++)
                    {
                        // 表达式树被序列化后重放：每个子任务用自己的局部变量，避免共享循环变量
                        var message = $"{name}#{i}";
                        if (delay > TimeSpan.Zero)
                            ctx.Schedule(() => DemoJobs.BatchItem(message), delay);
                        else if (i == failIndex)
                            ctx.Enqueue(() => DemoJobs.BatchItemFail(message));
                        else
                            ctx.Enqueue(() => DemoJobs.BatchItem(message));
                    }
                }, name);

                var extra = delay > TimeSpan.Zero ? $"，全部延时 {delay.TotalSeconds:0} 秒执行" : string.Empty;
                TempData["Feedback"] = failIndex > 0
                    ? $"批量任务已创建：BatchId = {batchId}，共 {count} 个子任务{extra}，第 {failIndex} 个会失败（可重试）"
                    : $"批量任务已创建：BatchId = {batchId}，共 {count} 个子任务{extra}";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"创建批量任务失败：{ex.Message}";
            }
            return RedirectToPage();
        }

        /// <summary>
        /// 并发执行测试数据：入队 N 个「慢任务」（占用工作线程指定秒数），
        /// 使面板「并发执行」视图实时出现 processing 任务；可选注册每分钟持续供给。
        /// </summary>
        public async Task<IActionResult> OnPostConcurrencyAsync()
        {
            try
            {
                var count = Math.Clamp(ConcurrencyCount, 1, 200);
                var seconds = Math.Clamp(ConcurrencySeconds, 1, 600);
                var stamp = DateTime.Now.ToString("HHmmss");

                for (var i = 1; i <= count; i++)
                {
                    // 表达式树序列化后重放：message 用局部变量，避免共享循环变量
                    var message = $"并发演示-{stamp}#{i}";
                    await WorkHelper.Manager.Enqueue(() => DemoJobs.SlowWork(message, seconds), message);
                }

                if (ContinuousFeed)
                {
                    await WorkHelper.Recurring.AddOrUpdate<DemoRecurringJob>(
                        FeedRecurringId,
                        job => job.SlowFeed(count, seconds),
                        "* * * * *",
                        "default",
                        new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Local },
                        "并发演示-持续供给");
                    TempData["Feedback"] =
                        $"已入队 {count} 个慢任务（每个 {seconds}s），并注册每分钟持续供给（周期任务 Id = {FeedRecurringId}）";
                }
                else
                {
                    TempData["Feedback"] =
                        $"已入队 {count} 个慢任务（每个 {seconds}s）：打开面板「并发执行」即可看到运行中任务";
                }
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"创建并发测试数据失败：{ex.Message}";
            }
            return RedirectToPage();
        }

        /// <summary>
        /// Awaiting（连续任务）测试：批量第 1 项为慢任务（父任务），其余项以
        /// AwaitingWorkState 挂在父任务上 —— 子任务先以 awaiting 状态落库，
        /// 父任务成功后由框架自动入队执行。
        /// </summary>
        public async Task<IActionResult> OnPostAwaitingAsync()
        {
            try
            {
                var name = Normalize(AwaitingName, "页面Awaiting连续任务测试");
                var seconds = Math.Clamp(AwaitingParentSeconds, 1, 600);
                var childCount = Math.Clamp(AwaitingChildCount, 1, 100);
                var parentMessage = $"{name}-父任务";

                var batchId = await WorkHelper.Batch.Create(ctx =>
                {
                    // 第 1 项：父任务（返回批量项 Id，供子任务挂靠；框架执行时解析为真实 WorkId）
                    var parentId = ctx.Enqueue(() => DemoJobs.SlowWork(parentMessage, seconds));

                    // 其余项：awaiting 状态，等父任务成功后自动入队
                    for (var i = 1; i <= childCount; i++)
                    {
                        // 表达式树序列化后重放：message 用局部变量，避免共享循环变量
                        var message = $"{name}-子任务#{i}";
                        ctx.Awaiting(() => DemoJobs.BatchItem(message), parentId);
                    }
                }, name);

                TempData["Feedback"] =
                    $"Awaiting 测试批量已创建：BatchId = {batchId}，父任务运行 {seconds} 秒，" +
                    $"{childCount} 个子任务先处于 awaiting，父任务成功后自动入队";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"创建 Awaiting 测试失败：{ex.Message}";
            }
            return RedirectToPage();
        }

        /// <summary>停止并发数据的持续供给（移除周期任务）</summary>
        public async Task<IActionResult> OnPostStopFeedAsync()
        {
            try
            {
                await WorkHelper.Recurring.RemoveIfExists(FeedRecurringId);
                TempData["Feedback"] = $"已停止持续供给（移除周期任务 {FeedRecurringId}）";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"停止持续供给失败：{ex.Message}";
            }
            return RedirectToPage();
        }

        /// <summary>
        /// saga 事务测试：三步正向编排；可指定某一步故意失败，
        /// 观察逆序补偿（失败步骤之前的步骤被回滚，之后的记为 Skipped）。
        /// </summary>
        public async Task<IActionResult> OnPostSagaAsync()
        {
            try
            {
                var name = Normalize(SagaName, "页面Saga事务测试");
                // CorrelationId 是幂等键：留空则用时间戳生成新事务；填同一值会命中既有 saga
                var correlationId = string.IsNullOrWhiteSpace(CorrelationId)
                    ? $"page-{DateTime.UtcNow:yyyyMMddHHmmssfff}"
                    : CorrelationId.Trim();

                // 新 API：流程定义在 DemoSaga.Configure 里，业务输入（失败步号、saga 名）随实例快照落库
                var sagaId = await WorkHelper.Saga.Start(new DemoSaga
                {
                    Corr = correlationId,
                    SagaName = name,
                    FailStep = FailStep,
                });

                TempData["Feedback"] = FailStep > 0
                    ? $"saga 已启动：SagaId = {sagaId}，CorrelationId = {correlationId}，第 {FailStep} 步将故意失败以触发补偿"
                    : $"saga 已启动：SagaId = {sagaId}，CorrelationId = {correlationId}（三步正常推进到 Completed）";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"启动 saga 失败：{ex.Message}";
            }
            return RedirectToPage();
        }

        private static string Normalize(string? value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
