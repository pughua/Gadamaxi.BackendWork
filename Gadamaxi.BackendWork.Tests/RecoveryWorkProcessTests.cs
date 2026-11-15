using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Works.States;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// P0-2 修复验证：Processing 卡死任务可被恢复重新入队。
    /// 修复前：执行节点崩溃后任务永久卡在 processing 状态。
    /// </summary>
    public class RecoveryWorkProcessTests : BackendWorkTestBase
    {
        public RecoveryWorkProcessTests() : base(processingTimeout: TimeSpan.Zero)
        {
        }

        [Fact]
        public async Task 卡死任务_被恢复为enqueued并重新入队()
        {
            var workId = await EnqueueSimpleWork();

            // 模拟任务进入 processing 后执行节点崩溃：
            // 先把任务置为 processing，再让状态时间超过 WorkProcessingTimeout（本测试设为 0）
            var data = await Query.GetWorkData(workId);
            Assert.NotNull(data);
            await WorkFactory.ChangeState(workId, new ProcessingWorkState("crashed-server", "test"), data!.StateName, data);

            // 确保状态时间严格早于 cutoff（避免时钟精度抖动导致不稳定）
            await Task.Delay(50);

            var recovery = new RecoveryWorkProcess(CreateLogger<RecoveryWorkProcess>());
            var scanned = await recovery.RecoverStaleWorks();

            Assert.Equal(1, scanned);

            var lastState = await Query.GetLastState(workId);
            Assert.NotNull(lastState);
            Assert.Equal(EnqueuedWorkState.Name, lastState!.StateName);

            // 状态机转 enqueued 时应重新发布队列消息
            Assert.True(await QueueRowExists(workId), "恢复后应重新发布队列消息");
        }

        [Fact]
        public async Task 未超时的processing任务_不恢复()
        {
            var workId = await EnqueueSimpleWork();

            var data = await Query.GetWorkData(workId);
            Assert.NotNull(data);
            await WorkFactory.ChangeState(workId, new ProcessingWorkState("live-server", "test"), data!.StateName, data);

            // 默认 WorkProcessingTimeout（10 分钟）：刚进入 processing 的任务不应被恢复
            Option.WorkProcessingTimeout = TimeSpan.FromMinutes(10);

            var recovery = new RecoveryWorkProcess(CreateLogger<RecoveryWorkProcess>());
            var scanned = await recovery.RecoverStaleWorks();

            Assert.Equal(0, scanned);

            var lastState = await Query.GetLastState(workId);
            Assert.Equal(ProcessingWorkState.Name, lastState!.StateName);
        }

        [Fact]
        public async Task 已完成任务_不恢复()
        {
            var workId = await EnqueueSimpleWork();

            var data = await Query.GetWorkData(workId);
            Assert.NotNull(data);
            await WorkFactory.ChangeState(workId, new ProcessingWorkState("srv", "test"), data!.StateName, data);
            await WorkFactory.ChangeState(workId, new SucceededWorkState(null, 1, "test"), ProcessingWorkState.Name, data);

            var recovery = new RecoveryWorkProcess(CreateLogger<RecoveryWorkProcess>());
            var scanned = await recovery.RecoverStaleWorks();

            Assert.Equal(0, scanned);
            Assert.Equal(SucceededWorkState.Name, (await Query.GetLastState(workId))!.StateName);
        }
    }
}
