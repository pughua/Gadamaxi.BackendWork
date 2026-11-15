using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Sagas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// saga 的端到端验证：启动真实后台服务（<c>AddBackendWork</c> 注册的 <see cref="IHostedService"/>），
    /// 由框架自己的调度链路（队列消费 → SagaStepRunner 还原实例执行 → 状态回写管线 → saga 推进进程）
    /// 把 saga 跑完，再验证监控查询（<see cref="ISagaManager.GetSaga"/> /
    /// <see cref="ISagaManager.GetSagaByCorrelationId"/> / <see cref="ISagaManager.GetSteps"/>）
    /// 在整条链路上读到的结果与真实执行完全一致。
    /// <para>
    /// 与 <c>SagaAdvanceTests</c>（手动逐条驱动进程）分工互补：那一层把「推进」与「执行」拆开、
    /// 对时序完全可控，用来定位链路断点；这一层不碰任何内部进程，只从公开 API 观察
    /// 「真实调度跑出来的是什么」，覆盖监控面板的读取路径。
    /// </para>
    /// </summary>
    public class SagaEndToEndTests : BackendWorkTestBase
    {
        private readonly ISagaManager _saga;

        /// <summary>
        /// 启动的是真实后台工作线程（进程无法被真正强杀），因此保留临时库：
        /// 删库会让残留线程反复重建已删除的库，反而污染后续用例。
        /// </summary>
        public SagaEndToEndTests() : base(deleteDatabaseOnDispose: false)
        {
            TestJobs.Reset();
            SagaTestJobs.Reset();

            Option.ServerName = "saga-e2e-server";
            // 队列消费与 saga 推进都得够快：saga 是严格串行的「一步一轮」，每一步都要
            // 「展开 → 入队 → 消费 → 回写」，轮询间隔直接乘在步骤数上
            Option.SagaPollingDelay = TimeSpan.FromMilliseconds(150);
            Option.WorkPollingDelay = TimeSpan.FromMilliseconds(300);
            Option.SchedulePollingDelay = TimeSpan.FromMilliseconds(300);
            // 少量工作线程即可：saga 严格串行、用不到高并发，而每派发一个工作循环之间都有
            // 随机 200~1000ms 的错峰，线程越多服务起得越慢。取消用例需要至少 2 个队列线程：
            // 一个被慢步骤占住，另一个才能执行补偿步骤
            Option.WorkerCount = 3;

            // 失败的步骤要尽快到终态，否则用例得等满重试退避（默认退避是 1 分钟）
            Option.WorkRetryCount = 1;
            Option.WorkRetryDelay = TimeSpan.Zero;

            // 后台工作线程是常驻循环，用例结束后仍可能空转。把与本例无关的进程调到极低频、
            // 卡死阈值调到极大，避免残留线程干扰后续用例（沿用 EndToEndSchedulingTests 的做法）
            Option.RecurringPollingDelay = TimeSpan.FromMinutes(10);
            Option.RecoveryPollingDelay = TimeSpan.FromMinutes(10);
            Option.CleanupPollingDelay = TimeSpan.FromMinutes(10);
            Option.SagaTimeoutPollingDelay = TimeSpan.FromMinutes(10);
            Option.WorkProcessingTimeout = TimeSpan.FromHours(10);
            // 停机只等很短时间：断言已经跑完，在途的慢步骤不值得为它多等
            Option.ShutdownTimeout = TimeSpan.FromSeconds(3);

            _saga = Provider.GetRequiredService<ISagaManager>();
        }

        [Fact]
        public async Task 端到端_成功链路_监控查询全程可读()
        {
            // 第一步返回订单号 → outputKey 采集进变量池；第二步无参读 this（实例还原 + 变量回填）
            var sagaId = await _saga.Start(new ScriptSaga
            {
                Corr = "e2e-ok",
                SagaName = "订单履约E2E",
                StepCount = 3,
                OutputKey = "outputValue",
                OutputValue = "order-42"
            });

            var host = await StartHostedServer();
            try
            {
                Assert.True(await WaitUntil(async () => (await _saga.GetSaga(sagaId))?.Status == SagaStatus.Completed,
                    TimeSpan.FromSeconds(60)), "真实调度应把 saga 推进到 Completed");
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
            }

            // 按 Id 与按业务键读到的是同一个实例，阶段与两个游标一致
            var saga = await _saga.GetSaga(sagaId);
            Assert.NotNull(saga);
            Assert.Equal("订单履约E2E", saga!.Name);
            Assert.Equal("e2e-ok", saga.CorrelationId);
            Assert.Equal(SagaStatus.Completed, saga.Status);
            Assert.Equal(3, saga.StepCount);
            // 正向游标越过最后一步 = 正向完成；补偿游标未动
            Assert.Equal(4, saga.CurrentIndex);
            Assert.Equal(0, saga.CompensationIndex);
            Assert.Equal(sagaId, (await _saga.GetSagaByCorrelationId("e2e-ok"))!.SagaId);
            // 变量池：运行时采集到的产出在
            Assert.Equal("order-42", saga.Variables["outputValue"]);

            // 步骤按序号升序返回，名字默认取方法名（D4），状态与展开出的工作 Id 都可回溯
            var steps = await _saga.GetSteps(sagaId);
            Assert.Equal(new[] { 1, 2, 3 }, steps.Select(t => t.Index).ToArray());
            Assert.Equal(new[] { "Forward1", "Forward2", "Forward3" },
                steps.Select(t => t.Name).ToArray());
            Assert.All(steps, t => Assert.Equal(SagaStepStatus.Succeeded, t.Status));
            Assert.All(steps, t => Assert.False(string.IsNullOrEmpty(t.ForwardWorkId)));
            // 正向成功不需要补偿，补偿工作一个都不该被展开
            Assert.All(steps, t => Assert.True(string.IsNullOrEmpty(t.CompensateWorkId)));

            // 步骤上的工作 Id 能在 Work 表里按步骤名回溯（监控面板的"下钻"路径）
            foreach (var step in steps)
            {
                Assert.Equal(step.Name, await WorkNameInDb(step.ForwardWorkId));
            }

            // 三步都真实执行过，且严格按声明顺序串行；第二步拿到的是第一步的返回值
            var records = SagaTestJobs.Records.ToArray();
            Assert.Contains("s1", records);
            Assert.Contains("s2:order-42", records);
            Assert.Contains("s3", records);
            Assert.True(Array.IndexOf(records, "s1") < Array.IndexOf(records, "s2:order-42"), "步骤必须串行执行");

            // 终态后移出调度缓存，不再被反复扫描
            Assert.False(await Query.CheckCacheSet(Helper.SagaKey, sagaId));

            // 监控查询的边界：不存在的 Id / 业务键一律返回 null，而不是抛错或返回空实例
            Assert.Null(await _saga.GetSaga("no-such-saga"));
            Assert.Null(await _saga.GetSagaByCorrelationId("no-such-correlation"));
        }

        [Fact]
        public async Task 端到端_失败链路_监控查询反映逆序补偿()
        {
            // 中间的步骤终态失败（重试耗尽），触发逆序补偿
            var sagaId = await _saga.Start(new ScriptSaga
            {
                Corr = "e2e-fail",
                SagaName = "中途失败E2E",
                StepCount = 3,
                FailAt = 2
            });

            var host = await StartHostedServer();
            try
            {
                Assert.True(await WaitUntil(async () => (await _saga.GetSaga(sagaId))?.Status == SagaStatus.Compensated,
                    TimeSpan.FromSeconds(60)), "真实调度应把失败的 saga 补偿到 Compensated");
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
            }

            var saga = await _saga.GetSaga(sagaId);
            Assert.NotNull(saga);
            Assert.Equal(SagaStatus.Compensated, saga!.Status);
            // 补偿游标回退到 0 = 补偿完成
            Assert.Equal(0, saga.CompensationIndex);

            var steps = await _saga.GetSteps(sagaId);
            Assert.Equal(SagaStepStatus.Compensated, steps[0].Status);  // 已成功的步骤被撤销
            Assert.Equal(SagaStepStatus.Failed, steps[1].Status);       // 失败步骤本身视为未生效，不补偿
            Assert.Equal(SagaStepStatus.Skipped, steps[2].Status);      // 还没轮到的步骤留痕为 Skipped
            Assert.Contains("will not be executed", steps[2].Message);
            // 失败原因能从监控查询读到，且是业务根因（不是反射调用的包装异常文本）
            Assert.Contains("s2 failed by test plan", steps[1].Message);

            // 只有第一步被补偿，且补偿工作可按「步骤名 + ~undo」回溯
            Assert.False(string.IsNullOrEmpty(steps[0].CompensateWorkId));
            Assert.True(string.IsNullOrEmpty(steps[1].CompensateWorkId));
            Assert.True(string.IsNullOrEmpty(steps[2].CompensateWorkId));
            Assert.Equal("Forward1~undo", await WorkNameInDb(steps[0].CompensateWorkId));

            // 未执行的步骤不会补跑，未生效的步骤也不会被补偿
            var records = SagaTestJobs.Records.ToArray();
            Assert.Contains("s1", records);
            Assert.Contains("c1", records);
            Assert.DoesNotContain("s3", records);
            Assert.DoesNotContain("c2", records);
            Assert.DoesNotContain("c3", records);
            Assert.True(Array.IndexOf(records, "s1") < Array.IndexOf(records, "c1"), "补偿必须在正向之后");

            Assert.False(await Query.CheckCacheSet(Helper.SagaKey, sagaId));
        }

        [Fact]
        public async Task 端到端_补偿失败转Aborted_人工续补后收敛为Compensated()
        {
            // 正向第 2 步失败 → 触发补偿；补偿第 1 步的失败用计数注入（连续失败）。
            // 不能用 FailCompensateAt：那是输入属性，会随快照落库，每次实例还原都带着，
            // 续补后依然恒败 —— 正好是"续补无法收敛"的假象
            SagaTestJobs.Fail("c1", 10);

            var sagaId = await _saga.Start(new ScriptSaga
            {
                Corr = "e2e-abort",
                SagaName = "补偿失败E2E",
                StepCount = 2,
                FailAt = 2
            });

            var host = await StartHostedServer();
            try
            {
                Assert.True(await WaitUntil(async () => (await _saga.GetSaga(sagaId))?.Status == SagaStatus.Aborted,
                    TimeSpan.FromSeconds(60)), "补偿终态失败后 saga 应停在 Aborted 等人工介入");

                // 监控查询要能看出「卡在哪一步、为什么」——这是人工介入的唯一依据
                var aborted = await _saga.GetSaga(sagaId);
                Assert.Contains("compensate step 1 failed", aborted!.Message);
                // 中止原因里带着底层异常消息（强类型读取，不依赖惰性填充的状态数据），
                // 这正是人工决定「是重试补偿还是人工回滚」的唯一依据
                Assert.Contains("c1 failed by test plan", aborted.Message);
                Assert.Equal(1, aborted.CompensationIndex);   // 补偿游标停在失败的那一步

                var abortedSteps = await _saga.GetSteps(sagaId);
                Assert.Equal(SagaStepStatus.CompensationFailed, abortedSteps[0].Status);
                Assert.Equal(SagaStepStatus.Failed, abortedSteps[1].Status);

                // 人工续补：先解除补偿作业的失败注入，再让 SagaManager 把失败的补偿步骤退回并唤醒
                SagaTestJobs.Fail("c1", 0);
                await RetryCompensationWithRetry(sagaId);

                Assert.True(await WaitUntil(async () => (await _saga.GetSaga(sagaId))?.Status == SagaStatus.Compensated,
                    TimeSpan.FromSeconds(60)), "续补后真实调度应把 saga 收敛到 Compensated");
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
            }

            var saga = await _saga.GetSaga(sagaId);
            Assert.Equal(SagaStatus.Compensated, saga!.Status);
            Assert.Equal(0, saga.CompensationIndex);

            var steps = await _saga.GetSteps(sagaId);
            // 失败的补偿步骤被重试并最终成功；失败的正向步骤保持 Failed（不重跑正向）
            Assert.Equal(SagaStepStatus.Compensated, steps[0].Status);
            Assert.Equal(SagaStepStatus.Failed, steps[1].Status);
            // 补偿确实被重试过：首次失败 + 续补成功
            Assert.True(SagaTestJobs.AttemptCount("c1") >= 2);

            Assert.False(await Query.CheckCacheSet(Helper.SagaKey, sagaId));
        }

        [Fact]
        public async Task 端到端_运行中取消_监控查询收敛到已补偿()
        {
            var sagaId = await _saga.Start(new ScriptSaga
            {
                Corr = "e2e-cancel",
                SagaName = "运行中取消E2E",
                StepCount = 3,
                // 慢步骤：留出"正在执行"的窗口，好在真实调度进行到一半时发起取消
                SlowMilliseconds = 3000
            });

            var host = await StartHostedServer();
            try
            {
                // 等到第一步已成功、第二步正在执行：此时取消才能覆盖「补偿已成功过的步骤」这条路径
                var running = await WaitUntil(async () =>
                {
                    var current = await _saga.GetSaga(sagaId);
                    if (current == null || current.Status != SagaStatus.Running || current.CurrentIndex != 2)
                    {
                        return false;
                    }

                    var steps = await _saga.GetSteps(sagaId);
                    return steps[0].Status == SagaStepStatus.Succeeded
                        && steps[1].Status == SagaStepStatus.Running;
                }, TimeSpan.FromSeconds(60));
                Assert.True(running, "saga 应被真实调度推进到第二步执行中");

                await CancelWithRetry(sagaId, "用户取消E2E");

                Assert.True(await WaitUntil(async () => (await _saga.GetSaga(sagaId))?.Status == SagaStatus.Compensated,
                    TimeSpan.FromSeconds(60)), "取消后真实调度应把 saga 补偿到 Compensated");
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
            }

            var saga = await _saga.GetSaga(sagaId);
            Assert.Equal(SagaStatus.Compensated, saga!.Status);
            Assert.Equal(0, saga.CompensationIndex);

            var steps = await _saga.GetSteps(sagaId);
            // 已成功过的第一步被真实补偿
            Assert.Equal(SagaStepStatus.Compensated, steps[0].Status);
            Assert.False(string.IsNullOrEmpty(steps[0].CompensateWorkId));
            Assert.Equal("Forward1~undo", await WorkNameInDb(steps[0].CompensateWorkId));
            // 还没轮到执行的第三步不该被展开
            Assert.Equal(SagaStepStatus.Init, steps[2].Status);
            Assert.True(string.IsNullOrEmpty(steps[2].ForwardWorkId));
            // 第二步的状态取决于慢步骤何时结束（取消只保证 saga 收敛），因此不做断言

            var records = SagaTestJobs.Records.ToArray();
            Assert.Contains("c1", records);
            Assert.DoesNotContain("s3", records);
        }

        #region helpers

        /// <summary>
        /// 走生产注册路径启动后台服务：<c>AddBackendWork</c> 注册的 <see cref="IHostedService"/>。
        /// </summary>
        private async Task<IHostedService> StartHostedServer()
        {
            var host = Provider.GetServices<IHostedService>().First();
            await host.StartAsync(CancellationToken.None);
            return host;
        }

        /// <summary>
        /// 取消要与推进进程争同一把 saga 锁，取不到锁 / 被抢先时抛 <see cref="BackendException"/>。
        /// 这是设计上的正常并发（人工干预是读后写，不能与推进交错），重试即可，不是故障。
        /// </summary>
        private async Task CancelWithRetry(string sagaId, string reason)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                try
                {
                    Assert.True(await _saga.Cancel(sagaId, reason), "取消应真的发起（saga 仍在运行）");
                    return;
                }
                catch (BackendException)
                {
                    await Task.Delay(50);
                }
            }

            throw new InvalidOperationException("取消未能在超时前发起。");
        }

        /// <summary>同 <see cref="CancelWithRetry"/>：续补也要与推进进程争 saga 锁。</summary>
        private async Task RetryCompensationWithRetry(string sagaId)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                try
                {
                    await _saga.RetryCompensation(sagaId);
                    return;
                }
                catch (BackendException)
                {
                    await Task.Delay(50);
                }
            }

            throw new InvalidOperationException("续补未能在超时前发起。");
        }

        #endregion
    }
}
