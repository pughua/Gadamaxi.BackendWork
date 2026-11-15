using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// P0-3 修复验证：任务方法不会被基础设施重试或消息重投导致重复执行。
    /// 修复前：Helper.Retry 包住整个任务执行，副作用产生后再失败会从头重跑任务。
    /// </summary>
    public class QueueWorkProcessTests : BackendWorkTestBase
    {
        private readonly QueueWorkProcess _process;
        private readonly WorkProcessContext _context;

        public QueueWorkProcessTests()
        {
            TestJobs.Reset();
            _process = new QueueWorkProcess(CreateLogger<QueueWorkProcess>());
            _context = NewProcessContext();
        }

        private static QueueInfo Info(string workId)
            => new QueueInfo { WorkId = workId, QueueName = Helper.DefaultQueue };

        [Fact]
        public async Task 任务执行成功后_消息重投_不再重复执行()
        {
            var workId = await EnqueueSimpleWork();

            var ok1 = await _process.Perform(_context, Info(workId));
            Assert.True(ok1);
            Assert.Equal(1, TestJobs.InvokeCount);

            // 模拟消息重新投递（可见性超时后的第二次 Perform）
            var ok2 = await _process.Perform(_context, Info(workId));
            Assert.True(ok2);

            // 修复点：已完成（Finish）的工作直接确认，任务方法只执行一次
            Assert.Equal(1, TestJobs.InvokeCount);
        }

        [Fact]
        public async Task processing状态的任务_消息重投_不会被重复执行()
        {
            var workId = await EnqueueSimpleWork();

            // 模拟其它 worker 正在执行（任务处于 processing 状态）
            var data = await Query.GetWorkData(workId);
            Assert.NotNull(data);
            await WorkFactory.ChangeState(workId, new ProcessingWorkState("another-worker", "test"), data!.StateName, data);

            var ok = await _process.Perform(_context, Info(workId));
            Assert.True(ok);

            // 修复点：processing 状态的任务被跳过并确认消息，不会重复执行
            Assert.Equal(0, TestJobs.InvokeCount);
        }

        [Fact]
        public async Task 业务失败_只走状态机重试_单次执行()
        {
            var workId = await EnqueueFailWork();

            var ok = await _process.Perform(_context, Info(workId));
            Assert.True(ok); // 失败已写入状态机，消息视为已处理

            Assert.Equal(1, TestJobs.InvokeCount);

            // 修复点：不再使用 Helper.Retry 包住任务执行，
            // 失败只记录一次 FailedWorkState，并由状态机转 ScheduledWorkState 退避重试
            Assert.Equal(1, await Query.CountState(workId, FailedWorkState.Name));
            Assert.Equal(1, await Query.CountState(workId, ScheduledWorkState.Name));
        }
    }
}
