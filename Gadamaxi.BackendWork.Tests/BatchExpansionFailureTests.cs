using System;
using System.Linq;
using System.Threading.Tasks;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 批量<b>展开阶段</b>异常的落库契约。
    /// <para>
    /// 与"子任务执行失败"不同，展开失败（例如批量项名非法导致 CreateWork 抛错）走的是
    /// <see cref="BatchWorkProcess"/> 的 exinfo 分支。该分支此前漏了 Commit，
    /// 延迟事务被静默丢弃：批量既不会变成 Failure 也不会终止，每轮被反复拉取，
    /// 而失败项已是 Processing 会被跳过 —— 失败对使用者完全不可见。
    /// </para>
    /// </summary>
    public class BatchExpansionFailureTests : BackendWorkTestBase
    {
        public BatchExpansionFailureTests()
        {
            // 展开流程结束后 BatchWorkProcess 会等一个调度轮询周期才返回，调小以缩短用例耗时
            Option.SchedulePollingDelay = TimeSpan.FromMilliseconds(100);
            Option.WorkPollingDelay = TimeSpan.FromMilliseconds(100);
            Option.RecurringPollingDelay = TimeSpan.FromMinutes(10);
            Option.RecoveryPollingDelay = TimeSpan.FromMinutes(10);
            Option.CleanupPollingDelay = TimeSpan.FromMinutes(10);
        }

        [Fact]
        public async Task 展开异常时_批量必须落到失败终态并落库()
        {
            var batchWork = new BatchWork { Name = "展开异常批量" };
            batchWork.Items.Add(new BatchItem
            {
                // 空名会让 WorkFactory.CreateWork 的 CheckWorkName 抛错，
                // 从而走"展开异常 → 标记批量失败"分支
                Name = string.Empty,
                Work = Workload.FromExpression(() => TestJobs.StaticWork()),
                Data = new BatchItemWorkData
                {
                    State = new WorkStateInfo
                    {
                        StateName = EnqueuedWorkState.Name,
                        Info = string.Empty,
                        CreateUtcTime = Helper.CurrentUtcTime(),
                        Data = new System.Collections.Generic.Dictionary<string, string>()
                    }
                }
            });
            // Count 由 BatchContext.AddWork 维护；这里直接构造批量，需要自己对齐，
            // 否则 RefreshBatchStatus 会以 0 个成功项 >= 0 个总项判成空批量成功
            batchWork.Count = batchWork.Items.Count;

            using (var storage = Provider.GetRequiredService<IWorkStorage>())
            {
                await storage.AddBatch(batchWork);
                // 登记进批量调度缓存（分数取过去时间，本轮即可被扫描到）
                await storage.AddCacheSet(Helper.BatchKey,
                    Helper.CurrentUtcTime().Add(TimeSpan.FromSeconds(-10)).UnixMillisecondStamp(),
                    batchWork.BatchId);
                await storage.Commit();
            }

            // IWorkProcess 现注册了多个（批量 / Saga 推进 / Saga 超时），
            // GetRequiredService<IWorkProcess>() 只会拿到最后注册的那个，故按类型显式挑选批量进程
            var process = Provider.GetServices<IWorkProcess>().OfType<BatchWorkProcess>().Single();
            await process.Execute(NewProcessContext());

            var batch = await Query.GetBatchById(batchWork.BatchId);
            var items = await Query.GetBatchItems(batchWork.BatchId, 0, 10);
            var item = Assert.Single(items);

            // 关键断言：批量失败状态必须真的写进库（修复前写入停留在延迟事务里被丢弃）
            Assert.True(batch != null && batch.Status == BatchStatus.Failure,
                $"批量状态={batch?.Status}，批量项=[状态={item.Status}, 子工作={item.WorkId}, 消息={item.Message}]");

            // 展开失败没有被吞掉：失败原因留在批量项上可供排查
            Assert.False(string.IsNullOrEmpty(item.Message), "展开失败的批量项应记录失败原因");
            Assert.Equal(BatchStatus.Processing, item.Status);
        }
    }
}
