using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Threading;
using Gadamaxi.BackendWork.Sagas;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 脚本化 saga 测试实现类：步骤与补偿都声明在本类（新 API 的形态），
    /// 行为由启动前填好的输入属性驱动（<see cref="StepCount"/>、<see cref="FailAt"/>……）。
    /// <para>
    /// 步骤方法名即步骤名（Forward1..Forward5 / Compensate1..Compensate5），
    /// 方法内统一记进 <see cref="SagaTestJobs"/> 的记录器，因此可以断言"执行了几次、顺序如何"。
    /// </para>
    /// <para>
    /// 变量传递走两条路径：第一步的返回值经 <c>outputKey: "outputValue"</c> 采集进变量池，
    /// 第二步<b>无参读 this</b>（变量池按名回填 <see cref="OutputValue"/> 属性）；
    /// 按名绑参数的路径由 <see cref="BindingSaga"/> 覆盖。
    /// </para>
    /// </summary>
    public class ScriptSaga : Saga<ScriptSaga>
    {
        /// <summary>业务关联键（幂等创建的键）</summary>
        public string Corr { get; set; } = string.Empty;

        /// <summary>saga 名（监控面板）；空串用于验证"名为空被拒绝"</summary>
        public string SagaName { get; set; } = "脚本流程";

        /// <summary>步骤数（1..5，0 = 空流程用于验证被拒绝）</summary>
        public int StepCount { get; set; }

        /// <summary>正向第几步失败（0 = 不失败）</summary>
        public int FailAt { get; set; }

        /// <summary>补偿第几步失败（0 = 不失败）</summary>
        public int FailCompensateAt { get; set; }

        /// <summary>第几步不可补偿（0 = 每步都有补偿）</summary>
        public int NoCompensateAt { get; set; }

        /// <summary>第二步的执行耗时（毫秒）：端到端用例靠它造"步骤正在执行"的窗口</summary>
        public int SlowMilliseconds { get; set; }

        /// <summary>第一步返回值的采集键（固定采集到 outputValue 属性）</summary>
        public string OutputKey { get; set; } = string.Empty;

        /// <summary>第一步的返回值；第二步经变量池回填后读 this 拿到</summary>
        public string OutputValue { get; set; } = string.Empty;

        /// <summary>启动时显式写入变量池的初始值（D5）</summary>
        public Dictionary<string, string> InitialVariables { get; set; } = new Dictionary<string, string>();

        public override string CorrelationId => Corr;

        public override string Name => SagaName;

        private static readonly Expression<Func<ScriptSaga, Task>>[] Forwards =
        {
            s => s.Forward1(), s => s.Forward2(), s => s.Forward3(), s => s.Forward4(), s => s.Forward5()
        };

        private static readonly Expression<Func<ScriptSaga, Task>>[] Compensates =
        {
            s => s.Compensate1(), s => s.Compensate2(), s => s.Compensate3(),
            s => s.Compensate4(), s => s.Compensate5()
        };

        protected override void Configure(SagaDefinition<ScriptSaga> def)
        {
            foreach (var kv in InitialVariables)
            {
                def.SetVariable(kv.Key, kv.Value);
            }

            for (var i = 1; i <= StepCount; i++)
            {
                if (i == NoCompensateAt)
                {
                    def.StepNoCompensate(Forwards[i - 1]);
                }
                else
                {
                    def.Step(Forwards[i - 1], Compensates[i - 1],
                        outputKey: i == 1 ? OutputKey : string.Empty);
                }
            }
        }

        // ===== 步骤实现（public 实例方法，数据全部来自 this —— 实例还原保证可用）=====

        /// <summary>第一步：返回 <see cref="OutputValue"/>，供 outputKey 采集进变量池</summary>
        public async Task<string> Forward1()
        {
            SagaTestJobs.Plan("s1", fail: FailAt == 1);
            await Task.CompletedTask;
            return OutputValue;
        }

        /// <summary>第二步：无参读 this（OutputValue 由变量池按名回填），验证实例还原</summary>
        public async Task Forward2()
        {
            var detail = string.IsNullOrEmpty(OutputValue) ? "s2" : $"s2:{OutputValue}";

            if (SlowMilliseconds > 0)
            {
                // 慢步骤：先记录（"标记出现"即代表已真的开始执行），再阻塞
                SagaTestJobs.Record(detail);
                await Task.Delay(SlowMilliseconds);
                SagaTestJobs.CheckFail("s2", FailAt == 2);
                return;
            }

            SagaTestJobs.Plan(detail, fail: FailAt == 2);
        }

        public Task Forward3()
        {
            SagaTestJobs.Plan("s3", fail: FailAt == 3);
            return Task.CompletedTask;
        }

        public Task Forward4()
        {
            SagaTestJobs.Plan("s4", fail: FailAt == 4);
            return Task.CompletedTask;
        }

        public Task Forward5()
        {
            SagaTestJobs.Plan("s5", fail: FailAt == 5);
            return Task.CompletedTask;
        }

        public Task Compensate1()
        {
            SagaTestJobs.Plan("c1", fail: FailCompensateAt == 1);
            return Task.CompletedTask;
        }

        public Task Compensate2()
        {
            SagaTestJobs.Plan("c2", fail: FailCompensateAt == 2);
            return Task.CompletedTask;
        }

        public Task Compensate3()
        {
            SagaTestJobs.Plan("c3", fail: FailCompensateAt == 3);
            return Task.CompletedTask;
        }

        public Task Compensate4()
        {
            SagaTestJobs.Plan("c4", fail: FailCompensateAt == 4);
            return Task.CompletedTask;
        }

        public Task Compensate5()
        {
            SagaTestJobs.Plan("c5", fail: FailCompensateAt == 5);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 按名绑参数的测试实现类：第二步的参数 <c>orderNo</c> 从变量池（第一步的 outputKey）绑定，
    /// 第三步的参数 <c>sku</c> 从输入属性绑定。
    /// </summary>
    public class BindingSaga : Saga<BindingSaga>
    {
        public string Corr { get; set; } = string.Empty;
        public string SagaName { get; set; } = "绑定流程";
        public string OrderNo { get; set; } = string.Empty;
        public string Sku { get; set; } = string.Empty;

        public override string CorrelationId => Corr;
        public override string Name => SagaName;

        protected override void Configure(SagaDefinition<BindingSaga> def)
        {
            // 第一步返回订单号 → outputKey: orderNo 采集进变量池
            def.Step(s => s.Produce(), s => s.UndoProduce(), outputKey: "orderNo");
            // 第二步的参数 orderNo 按名从变量池绑定（运行时产物优先）；
            // 带参方法声明处写 s.属性名 作绑定源注记（表达式树不允许零参调用带参方法）
            def.Step(s => s.Consume(s.OrderNo), s => s.UndoConsume());
            // 第三步的参数 sku 按名从输入属性绑定（启动快照）
            def.StepNoCompensate(s => s.Publish(s.Sku));
        }

        public Task<string> Produce()
        {
            SagaTestJobs.Plan("b1");
            return Task.FromResult(OrderNo);
        }

        public Task Consume(string orderNo)
        {
            SagaTestJobs.Plan($"b2:{orderNo}");
            return Task.CompletedTask;
        }

        public Task UndoProduce()
        {
            SagaTestJobs.Plan("bu1");
            return Task.CompletedTask;
        }

        public Task UndoConsume()
        {
            SagaTestJobs.Plan("bu2");
            return Task.CompletedTask;
        }

        public Task Publish(string sku)
        {
            SagaTestJobs.Plan($"b3:{sku}");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// saga 用例的记录器：所有脚本化 saga 的执行都记进这里（含失败的尝试），
    /// 因此可以断言"执行了几次、顺序如何"；失败用 <see cref="Fail"/> 按标记注入，独立于其它用例。
    /// </summary>
    public static class SagaTestJobs
    {
        /// <summary>按执行顺序记录（含失败的尝试），用于断言执行次数与补偿顺序</summary>
        public static readonly ConcurrentQueue<string> Records = new ConcurrentQueue<string>();

        private static readonly ConcurrentDictionary<string, int> FailTimes = new ConcurrentDictionary<string, int>();
        private static readonly ConcurrentDictionary<string, int> Attempts = new ConcurrentDictionary<string, int>();

        public static void Reset()
        {
            while (Records.TryDequeue(out _)) { }
            FailTimes.Clear();
            Attempts.Clear();
        }

        /// <summary>让携带该标记的作业从下一次起连续失败 <paramref name="times"/> 次</summary>
        public static void Fail(string marker, int times)
        {
            FailTimes[marker] = times;
        }

        /// <summary>某标记的累计执行次数（含失败）</summary>
        public static int AttemptCount(string marker)
        {
            return Attempts.TryGetValue(marker, out var count) ? count : 0;
        }

        /// <summary>记录一次执行（不注入失败）</summary>
        public static void Record(string marker, string? record = null)
        {
            Attempts.AddOrUpdate(marker, 1, (_, count) => count + 1);
            Records.Enqueue(record ?? marker);
        }

        /// <summary>记录一次执行，并按 fail 标记 / 计划注入抛出失败</summary>
        public static void Plan(string marker, bool fail = false, string? record = null)
        {
            Record(marker, record);

            if (fail || (FailTimes.TryGetValue(marker, out var remain) && remain > 0))
            {
                if (FailTimes.TryGetValue(marker, out var planned) && planned > 0)
                {
                    FailTimes[marker] = planned - 1;
                }

                throw new InvalidOperationException($"{marker} failed by test plan.");
            }
        }

        /// <summary>只按 fail 标记抛出失败（不重复记录）</summary>
        public static void CheckFail(string marker, bool fail)
        {
            if (fail)
            {
                throw new InvalidOperationException($"{marker} failed by test plan.");
            }
        }

        /// <summary>供 V1 校验用例引用的静态方法（步骤不许是静态方法）</summary>
        public static Task StaticJob()
        {
            return Task.CompletedTask;
        }
    }
}
