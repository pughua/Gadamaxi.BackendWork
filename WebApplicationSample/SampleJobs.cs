using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace WebApplicationSample
{
    /// <summary>
    /// 示例任务负载：演示页与 HTTP 端点创建的任务都落在这些方法上。
    /// 方法必须是 public、参数为可序列化常量 —— 框架按表达式树序列化后由工作进程重放。
    /// </summary>
    public static class SampleJobs
    {
        /// <summary>任务执行记录（内存演示用；生产请落日志/数据库）</summary>
        public static readonly ConcurrentQueue<string> Log = new();

        private static void Record(string message)
        {
            Log.Enqueue($"{DateTime.Now:HH:mm:ss} {message}");
            while (Log.Count > 50)
            {
                Log.TryDequeue(out _);
            }
            Console.WriteLine($"[sample] {message}");
        }

        /// <summary>即时/延时任务负载</summary>
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

        /// <summary>慢任务：占用工作线程指定秒数，便于在面板「并发执行」看到运行中任务</summary>
        public static async Task SlowWork(string message, int seconds)
        {
            Record($"slow start: {message}（{seconds}s）");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            Record($"slow end: {message}");
        }

        internal static void RecordPublic(string message) => Record(message);
    }

    /// <summary>
    /// 周期任务负载（实例类）：AddOrUpdate&lt;T&gt; 的类型参数不能是静态类，
    /// 框架触发时按类型实例化后重放方法调用。
    /// </summary>
    public class SampleRecurringJob
    {
        /// <summary>
        /// 注意：<paramref name="message"/> 是<b>注册时</b>求值的常量（表达式树按值序列化），
        /// 每轮都相同；方法体内取的 <c>DateTime.Now</c> 才是本次实际执行时间。
        /// </summary>
        public Task Tick(string message)
        {
            SampleJobs.RecordPublic($"recurring: {message} | 本次执行 {DateTime.Now:HH:mm:ss}");
            return Task.CompletedTask;
        }
    }
}
