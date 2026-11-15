using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works;
using Gadamaxi.BackendWork.Works.States;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 覆盖 <see cref="WorkActivator"/> 对各类返回值（同步 / Task / Task&lt;T&gt; / ValueTask / ValueTask&lt;T&gt;）的处理。
    /// 这两处最初都写错了：awaitable 判定用的是 Workload.Type（承载方法的类），
    /// 取返回值时又用 rs.GetType() == typeof(Task&lt;&gt;) 与开放泛型比较——都是恒 false，
    /// 结果是异步任务既不 await 也拿不到返回值。
    /// </summary>
    public class WorkActivatorTests : BackendWorkTestBase
    {
        public WorkActivatorTests()
        {
            ResultJobs.Reset();
        }

        #region helpers

        /// <summary>等价于真实入库路径：FromExpression 产出的是 (DeclaringType, MethodInfo, args)</summary>
        private static Workload Load(string methodName)
        {
            var type = typeof(ResultJobs);
            var method = type.GetMethod(methodName)
                ?? throw new ArgumentException($"ResultJobs 没有方法 {methodName}", nameof(methodName));
            return new Workload(type, method, Array.Empty<object>());
        }

        private static async Task<object?> InvokeWork(string methodName)
        {
            using (var act = new WorkActivator(Load(methodName)))
            {
                return await act.Invoke();
            }
        }

        #endregion

        #region 返回值

        [Fact]
        public async Task 同步方法_返回方法返回值()
        {
            Assert.Equal("sync-static", await InvokeWork(nameof(ResultJobs.StaticSync)));
            Assert.Equal("sync-instance", await InvokeWork(nameof(ResultJobs.InstanceSync)));
        }

        [Fact]
        public async Task Task泛型_返回真实结果()
        {
            Assert.Equal("async-static", await InvokeWork(nameof(ResultJobs.StaticAsync)));
            Assert.Equal("async-instance", await InvokeWork(nameof(ResultJobs.InstanceAsync)));
        }

        [Fact]
        public async Task Task非泛型_返回null且任务已执行完成()
        {
            Assert.Null(await InvokeWork(nameof(ResultJobs.StaticPlainTask)));
            Assert.True(ResultJobs.PlainTaskInvoked, "非泛型 Task 也应真正 await，而不是拿到未完成的 Task 就返回");
        }

        [Fact]
        public async Task 真正异步的方法_返回时任务体已跑完()
        {
            Assert.Equal("real-async", await InvokeWork(nameof(ResultJobs.ReallyAsync)));
            // 若没有 await，Invoke 返回时异步体尚未推进到这里
            Assert.True(ResultJobs.ReallyAsyncCompleted);
        }

        [Fact]
        public async Task ValueTask泛型_返回真实结果()
        {
            Assert.Equal("vt-generic", await InvokeWork(nameof(ResultJobs.ValueTaskResult)));
        }

        [Fact]
        public async Task ValueTask非泛型_返回null且任务已执行完成()
        {
            Assert.Null(await InvokeWork(nameof(ResultJobs.PlainValueTask)));
            Assert.True(ResultJobs.PlainValueTaskInvoked);
        }

        #endregion

        #region 异常传播

        [Fact]
        public async Task 异步方法抛异常_传播给调用方()
        {
            // 没有 await 时异常会被吞掉（永远写 succeeded），正是原先的失效形态
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await InvokeWork(nameof(ResultJobs.ThrowsAsync)));
        }

        [Fact]
        public async Task 同步方法抛异常_传播给调用方()
        {
            await Assert.ThrowsAnyAsync<Exception>(
                async () => await InvokeWork(nameof(ResultJobs.ThrowsSync)));
        }

        #endregion

        #region 根因守卫

        [Fact]
        public void awaitable判定必须用方法的返回类型_不能用Workload的Type()
        {
            var load = Load(nameof(ResultJobs.StaticAsync));

            // Workload.Type 是承载方法的类，恒不是 Task —— 这正是原先判定失效的原因
            Assert.Equal(typeof(ResultJobs), load.Type);
            Assert.Equal(typeof(Task<string>), load.MethodInfo.ReturnType);

            Assert.False(Helper.IsAwaitableType(load.Type));
            Assert.True(Helper.IsAwaitableType(load.MethodInfo.ReturnType));
        }

        [Fact]
        public void 封闭泛型类型_与开放泛型定义不相等()
        {
            // rs.GetType() == typeof(Task<>) 恒为 false，必须走 GetGenericTypeDefinition
            Assert.NotEqual(typeof(Task<>), Task.FromResult("x").GetType());
            Assert.Equal(typeof(Task<>), Task.FromResult("x").GetType().GetGenericTypeDefinition());
        }

        #endregion

        #region 链路验证：返回值是否真的落进成功状态

        [Fact]
        public async Task 队列执行_Task泛型任务的返回值写入成功状态()
        {
            var workId = await Manager.Enqueue(() => ResultJobs.StaticAsync(), "异步返回值任务");

            var process = new QueueWorkProcess(CreateLogger<QueueWorkProcess>());
            await process.Perform(NewProcessContext(),
                new QueueInfo { WorkId = workId, QueueName = Helper.DefaultQueue });

            var state = await Query.GetLastState(workId);
            var succeeded = Assert.IsType<SucceededWorkState>(state);
            Assert.Equal(SucceededWorkState.Name, succeeded.StateName);
            Assert.Equal("async-static", succeeded.Result);
        }

        [Fact]
        public async Task 队列执行_异步任务失败时写入失败状态()
        {
            Option.WorkRetryCount = 0;

            var workId = await Manager.Enqueue(() => ResultJobs.ThrowsAsync(), "异步失败任务");

            var process = new QueueWorkProcess(CreateLogger<QueueWorkProcess>());
            await process.Perform(NewProcessContext(),
                new QueueInfo { WorkId = workId, QueueName = Helper.DefaultQueue });

            var state = await Query.GetLastState(workId);
            Assert.IsType<FailedWorkState>(state);
        }

        #endregion
    }

    /// <summary>覆盖各种返回值形态的作业，供 <see cref="WorkActivatorTests"/> 使用</summary>
    public class ResultJobs
    {
        public static bool PlainTaskInvoked;
        public static bool ReallyAsyncCompleted;
        public static bool PlainValueTaskInvoked;

        public static void Reset()
        {
            PlainTaskInvoked = false;
            ReallyAsyncCompleted = false;
            PlainValueTaskInvoked = false;
        }

        public static string StaticSync() => "sync-static";
        public string InstanceSync() => "sync-instance";

        public static Task<string> StaticAsync() => Task.FromResult("async-static");
        public Task<string> InstanceAsync() => Task.FromResult("async-instance");

        public static async Task<string> ReallyAsync()
        {
            await Task.Delay(50);
            ReallyAsyncCompleted = true;
            return "real-async";
        }

        public static Task StaticPlainTask()
        {
            PlainTaskInvoked = true;
            return Task.CompletedTask;
        }

        public static ValueTask<string> ValueTaskResult() => new ValueTask<string>("vt-generic");

        public static ValueTask PlainValueTask()
        {
            PlainValueTaskInvoked = true;
            return default;
        }

        public static async Task<string> ThrowsAsync()
        {
            await Task.Delay(1);
            throw new InvalidOperationException("async job failed.");
        }

        public static string ThrowsSync() => throw new InvalidOperationException("sync job failed.");
    }
}
