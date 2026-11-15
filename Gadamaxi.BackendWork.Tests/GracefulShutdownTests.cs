using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Works;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// #9 优雅停机协议：停取新任务 → 等在途任务完成 → 超时放弃。
    /// 修复前 Stop（旧名 StartStop）只取消停止令牌就立即返回（Task.CompletedTask），
    /// 停机时正在执行的在途任务被直接终止，工作结果也不会落库。
    /// </summary>
    public class GracefulShutdownTests : BackendWorkTestBase
    {
        // 启动的是真实后台线程（进程无法被真正停止），保留库文件避免残留线程重建已删除的库
        public GracefulShutdownTests() : base(deleteDatabaseOnDispose: false)
        {
            Option.WorkPollingDelay = TimeSpan.FromMilliseconds(200);
        }

        [Fact]
        public async Task Stop_等待在途任务执行完成()
        {
            Option.ShutdownTimeout = TimeSpan.FromSeconds(30);
            TestJobs.Reset();
            var workId = await Manager.Enqueue(() => TestJobs.SlowWork("slow-1", 2000), "slow-work", Helper.DefaultQueue);

            var server = Provider.GetRequiredService<IWorkServer>();
            await server.Run();
            try
            {
                // 等到任务真的被取走并开始执行，确保停机时确实有任务在途
                var started = await WaitUntil(async () => await StateNameOf(workId) == ProcessingWorkState.Name,
                    TimeSpan.FromSeconds(30));
                Assert.True(started, "任务应已被工作线程取走并开始执行");
            }
            finally
            {
                await server.Stop();
            }

            // 停机返回时，在途任务已经跑完并落库，而不是被中途放弃
            Assert.Contains("slow-1", TestJobs.Records);
            Assert.Equal(SucceededWorkState.Name, await StateNameOf(workId));
        }

        [Fact]
        public async Task Stop_超时后放弃等待_不无限阻塞()
        {
            // 超时远小于任务执行时间：停机应按时返回，而不是死等
            Option.ShutdownTimeout = TimeSpan.FromMilliseconds(200);
            TestJobs.Reset();
            var workId = await Manager.Enqueue(() => TestJobs.SlowWork("slow-2", 4000), "slow-work", Helper.DefaultQueue);

            var server = Provider.GetRequiredService<IWorkServer>();
            await server.Run();
            try
            {
                var started = await WaitUntil(async () => await StateNameOf(workId) == ProcessingWorkState.Name,
                    TimeSpan.FromSeconds(30));
                Assert.True(started, "任务应已被工作线程取走并开始执行");
            }
            finally
            {
                var watch = Stopwatch.StartNew();
                await server.Stop();
                watch.Stop();

                // 超时后放弃等待：远小于任务本身的 4 秒执行时间
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3),
                    $"超时后应立即返回，实际耗时 {watch.ElapsedMilliseconds}ms");
            }
        }

        private async Task<string?> StateNameOf(string workId)
        {
            var data = await Query.GetWorkData(workId);
            return data?.StateName;
        }
    }
}
