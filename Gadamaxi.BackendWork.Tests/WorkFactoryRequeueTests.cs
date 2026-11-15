using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Works.States;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 通用重投实现 WorkFactory.RequeueWork 验证：
    /// 该方法被 DelayedWorkProcess（延时调度）与 RecoveryWorkProcess（卡死恢复）共用。
    /// </summary>
    public class WorkFactoryRequeueTests : BackendWorkTestBase
    {
        [Fact]
        public async Task 已存在的工作_重投成功_转为enqueued并发布消息()
        {
            var workId = await EnqueueSimpleWork();

            var requeued = await WorkFactory.RequeueWork(Query, Lock, workId, "test-trigger");

            Assert.True(requeued);
            var lastState = await Query.GetLastState(workId);
            Assert.Equal(EnqueuedWorkState.Name, lastState!.StateName);
            Assert.True(await QueueRowExists(workId));
        }

        [Fact]
        public async Task 已完成的工作_不重投()
        {
            var workId = await EnqueueSimpleWork();

            var data = await Query.GetWorkData(workId);
            Assert.NotNull(data);
            await WorkFactory.ChangeState(workId, new SucceededWorkState(null, 1, "test"), data!.StateName, data);

            var requeued = await WorkFactory.RequeueWork(Query, Lock, workId, "test-trigger");

            Assert.False(requeued);
            Assert.Equal(SucceededWorkState.Name, (await Query.GetLastState(workId))!.StateName);
        }

        [Fact]
        public async Task 不满足stateCheck条件_不重投()
        {
            var workId = await EnqueueSimpleWork();

            var requeued = await WorkFactory.RequeueWork(Query, Lock, workId, "test-trigger",
                last => false);

            Assert.False(requeued);
            Assert.Equal(EnqueuedWorkState.Name, (await Query.GetLastState(workId))!.StateName);
        }

        [Fact]
        public async Task 工作不存在_返回false不抛异常()
        {
            var requeued = await WorkFactory.RequeueWork(Query, Lock, "not-exist-work-id", "test-trigger");

            Assert.False(requeued);
        }
    }
}
