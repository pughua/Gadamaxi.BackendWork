using System.Collections.Concurrent;
using System.Threading.Tasks;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Sagas;
using Gadamaxi.BackendWork.Works;

namespace WebApplicationTest
{
    /// <summary>
    /// 演示任务负载：Razor 演示页（/demotasks）创建的任务都落在这些方法上。
    /// 方法必须是 public、参数为可序列化常量 —— 框架按表达式树序列化后由工作进程重放。
    /// </summary>
    public static class DemoJobs
    {
        /// <summary>任务执行记录（内存演示用，仅本页展示；生产请落日志/数据库）</summary>
        public static readonly ConcurrentQueue<string> Log = new();

        private static void Record(string message)
        {
            Log.Enqueue($"{DateTime.Now:HH:mm:ss} {message}");
            while (Log.Count > 50)
            {
                Log.TryDequeue(out _);
            }
            Console.WriteLine($"[demo] {message}");
        }

        /// <summary>即时任务负载</summary>
        public static Task Hello(string message)
        {
            Record($"hello: {message}");
            return Task.CompletedTask;
        }

        /// <summary>批量任务的子任务负载</summary>
        public static Task BatchItem(string message)
        {
            Record($"batch: {message}");
            return Task.CompletedTask;
        }

        /// <summary>批量任务中故意失败的子任务：用于测试批量重试（只重跑未成功的子任务）</summary>
        public static Task BatchItemFail(string message)
        {
            Record($"batch: {message}（故意失败）");
            throw new InvalidOperationException($"批量子任务 {message} 故意失败");
        }

        /// <summary>周期任务负载</summary>
        public static Task RecurringTick(string message)
        {
            Record($"recurring: {message}");
            return Task.CompletedTask;
        }

        /// <summary>
        /// 慢任务：占用工作线程指定秒数。用于给面板「并发执行」视图提供实时数据
        /// （任务处于 processing 状态时才会出现在此视图）。
        /// </summary>
        public static async Task SlowWork(string message, int seconds)
        {
            Record($"slow start: {message}（{seconds}s）");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            Record($"slow end: {message}");
        }

        /// <summary>saga 正向步骤</summary>
        public static Task SagaForward(string step)
        {
            Record($"saga 正向执行 {step}");
            return Task.CompletedTask;
        }

        /// <summary>saga 补偿步骤</summary>
        public static Task SagaCompensate(string step)
        {
            Record($"saga 补偿 {step}");
            return Task.CompletedTask;
        }

        /// <summary>故意失败的步骤：用于触发 saga 回滚（补偿）路径</summary>
        public static Task SagaFail(string step)
        {
            Record($"saga 正向执行 {step}（故意失败）");
            throw new InvalidOperationException($"步骤 {step} 故意失败，触发 saga 补偿");
        }

        internal static void RecordPublic(string message) => Record(message);
    }

    /// <summary>
    /// 周期任务负载（实例类）：AddOrUpdate&lt;T&gt; 的类型参数不能是静态类，
    /// 框架触发时按类型实例化后重放方法调用。
    /// </summary>
    public class DemoRecurringJob
    {
        public Task RecurringTick(string message)
        {
            DemoJobs.RecordPublic($"recurring: {message}");
            return Task.CompletedTask;
        }

        /// <summary>
        /// 并发数据持续供给：每轮入队 count 个慢任务，保证面板「并发执行」视图长期有运行中任务。
        /// 方法体在运行时执行，不受表达式序列化限制（被序列化的只是这次方法调用本身）。
        /// </summary>
        public async Task SlowFeed(int count, int seconds)
        {
            var stamp = DateTime.Now.ToString("HHmmss");
            for (var i = 1; i <= count; i++)
            {
                var message = $"feed-{stamp}#{i}";
                await WorkHelper.Manager.Enqueue(
                    () => DemoJobs.SlowWork(message, seconds), $"并发演示-供给#{stamp}-{i}");
            }
            DemoJobs.RecordPublic($"slow feed: 入队 {count} 个慢任务，每个 {seconds}s");
        }
    }

    /// <summary>
    /// saga 演示实现类：步骤与补偿都声明为本类实例方法（新 API 由编译器保证），
    /// 「哪一步故意失败」由启动前填好的输入属性 <see cref="FailStep"/> 在执行期决定，
    /// 与测试侧 <c>ScriptSaga</c> 的脚本化思路一致。
    /// </summary>
    public class DemoSaga : Saga<DemoSaga>
    {
        /// <summary>业务关联键（幂等创建的键）</summary>
        public string Corr { get; set; } = string.Empty;

        /// <summary>saga 名（监控面板显示）</summary>
        public string SagaName { get; set; } = "页面Saga事务测试";

        /// <summary>第几步故意失败（0 = 三步全部正常）</summary>
        public int FailStep { get; set; }

        public override string CorrelationId => Corr;

        public override string Name => SagaName;

        protected override void Configure(SagaDefinition<DemoSaga> def)
        {
            def.Step(s => s.Step1(), s => s.Undo1());
            def.Step(s => s.Step2(), s => s.Undo2());
            def.Step(s => s.Step3(), s => s.Undo3());
        }

        public Task Step1() => DemoJobs.SagaForward("s1");

        public Task Step2() => FailStep == 2 ? DemoJobs.SagaFail("s2") : DemoJobs.SagaForward("s2");

        public Task Step3() => FailStep == 3 ? DemoJobs.SagaFail("s3") : DemoJobs.SagaForward("s3");

        public Task Undo1() => DemoJobs.SagaCompensate("c1");

        public Task Undo2() => DemoJobs.SagaCompensate("c2");

        public Task Undo3() => DemoJobs.SagaCompensate("c3");
    }
}
