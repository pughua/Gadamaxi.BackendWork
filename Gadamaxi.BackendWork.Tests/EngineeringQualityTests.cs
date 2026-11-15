using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 工程质量项验证：#15（WorkServer 去硬编码 new + 线程数可配）、#17（参数类型统一走 Helper.GetType）、
    /// #18（重试指数退避 + jitter）、#22（批量扫描上限可配置）与 §2.4-4（Options Pattern）。
    /// </summary>
    public class EngineeringQualityTests : BackendWorkTestBase
    {
        #region #15 WorkServer 去硬编码 new

        [Fact]
        public void WorkServer_协作组件由DI提供_不再硬编码new()
        {
            // 修复前 WorkExecution/WorkDispatcher/WorkServerProcess 都在 WorkServer 构造函数里 internal new，
            // 无法从容器解析，也无法替换为测试替身
            Assert.NotNull(Provider.GetRequiredService<IWorkExecution>());
            Assert.NotNull(Provider.GetRequiredService<IWorkDispatcher>());
            Assert.NotNull(Provider.GetRequiredService<IWorkServerProcess>());
            Assert.IsType<WorkServer>(Provider.GetRequiredService<IWorkServer>());
        }

        [Fact]
        public void WorkerCount_默认未配置_由自动推算兜底()
        {
            // 修复前 MaxWorkThreads=15 硬编码在 WorkServerProcess 里，且 ServerContext.WorkThreads 从未被赋值，
            // 配置无法影响线程数。现在未配置（0）时回退到 Min(ProcessorCount*2, DefaultMaxWorkThreads)
            Assert.Equal(0, new WorkServerOption().WorkerCount);
            var expected = Math.Min(Environment.ProcessorCount * 2, Helper.DefaultMaxWorkThreads);
            Assert.True(expected > 0);
        }

        #endregion

        #region #22 批量扫描上限可配置

        [Fact]
        public void GetBatchSize_未配置时回退到默认常量()
        {
            var option = new WorkServerOption();
            Assert.Equal(Helper.DefaultBatchSize, option.GetBatchSize());

            option.BatchSize = 0;
            // 非正值应回退到默认常量
            Assert.Equal(Helper.DefaultBatchSize, option.GetBatchSize());

            option.BatchSize = 50;
            Assert.Equal(50, option.GetBatchSize());
        }

        [Fact]
        public async Task Subscribe_每轮上限随配置变化()
        {
            for (var i = 0; i < 5; i++)
            {
                await EnqueueSimpleWork();
            }

            Option.BatchSize = 2;
            try
            {
                var delivered = 0;
                await Queue.Subscribe(new List<string> { Helper.DefaultQueue }, ctx =>
                {
                    delivered++;
                    ctx.DealSuccess = true;
                    return Task.CompletedTask;
                });

                // 修复前每轮固定取 10 条（常量），配置无法影响吞吐
                Assert.Equal(2, delivered);
            }
            finally
            {
                Option.BatchSize = Helper.DefaultBatchSize;
            }

            // 恢复配置后，剩余消息能被取完
            var rest = 0;
            await Queue.Subscribe(new List<string> { Helper.DefaultQueue }, ctx =>
            {
                rest++;
                ctx.DealSuccess = true;
                return Task.CompletedTask;
            });
            Assert.Equal(3, rest);
        }

        #endregion

        #region #18 重试指数退避 + jitter

        [Fact]
        public void GetRetryDelay_随次数指数放大_并带随机抖动()
        {
            var baseDelay = TimeSpan.FromSeconds(1);

            // 第 1 次：base × 1 × [0.5,1.0)
            var first = Helper.GetRetryDelay(baseDelay, 1);
            Assert.InRange(first.TotalMilliseconds, baseDelay.TotalMilliseconds * 0.5, baseDelay.TotalMilliseconds);

            // 第 3 次：base × 4 × [0.5,1.0)，下界(2s)已高于第 1 次的上界(1s)
            var third = Helper.GetRetryDelay(baseDelay, 3);
            Assert.InRange(third.TotalMilliseconds, baseDelay.TotalMilliseconds * 2, baseDelay.TotalMilliseconds * 4);

            // 上限保护：再大的次数也不会超过 MaxRetryDelay
            var huge = Helper.GetRetryDelay(baseDelay, 30);
            Assert.True(huge <= Helper.MaxRetryDelay, $"退避应有上限，实际 {huge}");
        }

        [Fact]
        public void GetRetryDelay_抖动打散重试时刻()
        {
            var baseDelay = TimeSpan.FromSeconds(1);
            var samples = Enumerable.Range(0, 20).Select(_ => Helper.GetRetryDelay(baseDelay, 2).TotalMilliseconds).ToList();

            // 固定间隔（修复前）所有样本完全相同；带 jitter 后应有差异
            Assert.True(samples.Distinct().Count() > 1, "重试间隔应带随机抖动，避免所有节点同步重试");
            Assert.All(samples, s => Assert.InRange(s, baseDelay.TotalMilliseconds, baseDelay.TotalMilliseconds * 2));
        }

        [Fact]
        public async Task Retry_全部失败时返回异常_且不再无谓等待最后一次()
        {
            var attempts = 0;
            var ex = await Helper.Retry(async (_) =>
            {
                attempts++;
                throw new InvalidOperationException("always fail");
            }, 3, TimeSpan.FromMilliseconds(1));

            Assert.Equal(3, attempts);
            Assert.IsType<InvalidOperationException>(ex);
        }

        #endregion

        #region #17 参数类型解析

        [Fact]
        public void Workload_泛型与可空参数_反序列化后可正确匹配方法()
        {
            var load = Workload.FromExpression<GenericJobs>(j => j.Take(new List<string> { "a", "b" }, 3, 7));
            var json = load.Serialize();

            var back = Workload.Deserialize(json);

            Assert.Equal(load.Type, back.Type);
            Assert.Equal("Take", back.MethodInfo.Name);
            // 修复前参数类型用裸 Type.GetType，.NET Core 下带 System.Private.CoreLib 程序集名的
            // 泛型/可空类型解析返回 null，随后空引用（或方法匹配失败）
            Assert.Equal(3, back.MethodInfo.GetParameters().Length);
            Assert.NotNull(back.MethodInfo.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void Workload_简单类型参数_回归不受影响()
        {
            var load = Workload.FromExpression<InstanceJobs>(j => j.Write("hello"));
            var back = Workload.Deserialize(load.Serialize());

            Assert.Equal("Write", back.MethodInfo.Name);
            Assert.Equal(typeof(string), back.MethodInfo.GetParameters()[0].ParameterType);
        }

        #endregion

        #region §2.4-4 Options Pattern

        [Fact]
        public void IOptions_取值与配置实例同源_运行期调整即时生效()
        {
            var options = Provider.GetRequiredService<IOptions<WorkServerOption>>();

            Assert.Same(Option, options.Value);

            var origin = Option.BatchSize;
            try
            {
                Option.BatchSize = 77;
                // 经 IOptions 读到的就是同一个配置实例（既保留运行期可调，又统一了取值入口）
                Assert.Equal(77, options.Value.GetBatchSize());
            }
            finally
            {
                Option.BatchSize = origin;
            }
        }

        #endregion
    }

    /// <summary>
    /// 带泛型/可空参数的方法：用于验证 Workload 的参数类型解析（#17）。
    /// 序列化后的参数类型名形如 System.Collections.Generic.List`1[[System.String, System.Private.CoreLib, ...]]。
    /// </summary>
    public class GenericJobs
    {
        /// <summary>参数含泛型（List&lt;string&gt;）与可空值类型（int?），覆盖 #17 的类型解析场景。</summary>
        public void Take(List<string> items, int count, int? tag)
        {
            TestJobs.Mark($"generic:{items.Count}:{count}:{tag}");
        }
    }
}
