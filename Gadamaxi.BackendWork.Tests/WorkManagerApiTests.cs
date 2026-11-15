using System;
using System.Threading.Tasks;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works;
using Gadamaxi.BackendWork.Works.States;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// IWorkManager.Create 与 WorkManagerExtention 全部扩展方法的落库契约验证：
    /// AddWork、Enqueue（4 个表达式重载）、Schedule（Workload + 4 个表达式重载）、
    /// Awaiting（Workload + 4 个表达式重载）。
    /// 断言落在"创建结果"上：工作行、任务名、初始状态、队列路由、定时任务登记、连续任务登记。
    /// </summary>
    public class WorkManagerApiTests : BackendWorkTestBase
    {
        private const string CustomQueue = "custom-queue";

        #region IWorkManager.Create / AddWork

        [Fact]
        public async Task Create_接口方法_按传入的初始状态落库()
        {
            var ctx = new CreateWorkContext(
                Workload.FromExpression(() => TestJobs.StaticWork()),
                new EnqueuedWorkState(string.Empty, Helper.DefaultQueue));

            var workId = await Manager.Create(ctx, "接口直接创建");

            var data = await AssertCreated(workId, "接口直接创建", EnqueuedWorkState.Name);
            Assert.Equal(Helper.DefaultQueue, await QueueNameOf(workId));
            Assert.True(await QueueRowExists(workId), "入队状态应发布队列消息");
            Assert.NotNull(data);
        }

        [Fact]
        public async Task AddWork_自定义状态_按状态落库()
        {
            var work = Workload.FromExpression(() => TestJobs.StaticWork());

            var workId = await Manager.AddWork(work,
                new ScheduledWorkState(TimeSpan.FromMinutes(5), "test"), "自定义状态任务");

            await AssertCreated(workId, "自定义状态任务", ScheduledWorkState.Name);
            Assert.False(await QueueRowExists(workId), "定时任务创建时不入队");
            Assert.True(await Query.CheckCacheSet(ScheduledWorkState.Name, workId), "定时任务应登记到调度缓存");
        }

        [Fact]
        public async Task AddWork_work为空_抛异常()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => Manager.AddWork(null!, new EnqueuedWorkState(string.Empty, Helper.DefaultQueue), "空工作"));
        }

        #endregion

        #region Enqueue

        [Fact]
        public async Task Enqueue_静态同步Action_不传队列时回退默认队列()
        {
            var workId = await Manager.Enqueue(() => TestJobs.StaticWork(), "静态同步任务");

            var data = await AssertCreated(workId, "静态同步任务", EnqueuedWorkState.Name);
            Assert.Equal(Helper.DefaultQueue, await QueueNameOf(workId));
            // 队列同时写入负载内容：任务失败重试、重投时才能回到同一个队列
            Assert.Equal(Helper.DefaultQueue, data.Content.Queue);
        }

        [Fact]
        public async Task Enqueue_静态异步FuncTask_指定队列()
        {
            var workId = await Manager.Enqueue(() => TestJobs.StaticWorkAsync(), "静态异步任务", CustomQueue);

            var data = await AssertCreated(workId, "静态异步任务", EnqueuedWorkState.Name);
            Assert.Equal(CustomQueue, await QueueNameOf(workId));
            Assert.Equal(CustomQueue, data.Content.Queue);
        }

        [Fact]
        public async Task Enqueue_实例同步ActionT_类型方法名与参数完整序列化()
        {
            var workId = await Manager.Enqueue<InstanceJobs>(j => j.Write("hello"), "实例同步任务");

            var data = await AssertCreated(workId, "实例同步任务", EnqueuedWorkState.Name);
            Assert.Equal(Helper.DefaultQueue, await QueueNameOf(workId));

            var work = Workload.Deserialize(data.Content);
            Assert.Equal(typeof(InstanceJobs), work.Type);
            Assert.Equal(nameof(InstanceJobs.Write), work.MethodInfo.Name);
            Assert.Equal("hello", work.Arguments[0]);
        }

        [Fact]
        public async Task Enqueue_实例异步FuncTTask_指定队列且参数完整序列化()
        {
            var workId = await Manager.Enqueue<InstanceJobs>(j => j.WriteAsync("world"), "实例异步任务", CustomQueue);

            var data = await AssertCreated(workId, "实例异步任务", EnqueuedWorkState.Name);
            Assert.Equal(CustomQueue, await QueueNameOf(workId));
            Assert.Equal(CustomQueue, data.Content.Queue);

            var work = Workload.Deserialize(data.Content);
            Assert.Equal(typeof(InstanceJobs), work.Type);
            Assert.Equal(nameof(InstanceJobs.WriteAsync), work.MethodInfo.Name);
            Assert.Equal("world", work.Arguments[0]);
        }

        #endregion

        #region Schedule

        [Fact]
        public async Task Schedule_Workload重载_进入定时调度()
        {
            var work = Workload.FromExpression(() => TestJobs.StaticWork());

            var workId = await Manager.Schedule(work, TimeSpan.FromMinutes(1), "定时任务-负载");

            await AssertScheduled(workId, "定时任务-负载");
        }

        [Fact]
        public async Task Schedule_静态同步Action_进入定时调度()
        {
            var workId = await Manager.Schedule(() => TestJobs.StaticWork(), TimeSpan.FromMinutes(1), "定时任务-静态同步");

            await AssertScheduled(workId, "定时任务-静态同步");
        }

        [Fact]
        public async Task Schedule_实例同步ActionT_进入定时调度()
        {
            var workId = await Manager.Schedule<InstanceJobs>(j => j.Write("delay"),
                TimeSpan.FromMinutes(1), "定时任务-实例同步");

            await AssertScheduled(workId, "定时任务-实例同步");

            var data = await Query.GetWorkData(workId);
            var work = Workload.Deserialize(data!.Content);
            Assert.Equal(nameof(InstanceJobs.Write), work.MethodInfo.Name);
            Assert.Equal("delay", work.Arguments[0]);
        }

        [Fact]
        public async Task Schedule_静态异步FuncTask_进入定时调度()
        {
            var workId = await Manager.Schedule(() => TestJobs.StaticWorkAsync(), TimeSpan.FromMinutes(1), "定时任务-静态异步");

            await AssertScheduled(workId, "定时任务-静态异步");
        }

        [Fact]
        public async Task Schedule_实例异步FuncTTask_进入定时调度()
        {
            var workId = await Manager.Schedule<InstanceJobs>(j => j.WriteAsync("delay-async"),
                TimeSpan.FromMinutes(1), "定时任务-实例异步");

            await AssertScheduled(workId, "定时任务-实例异步");

            var data = await Query.GetWorkData(workId);
            var work = Workload.Deserialize(data!.Content);
            Assert.Equal(nameof(InstanceJobs.WriteAsync), work.MethodInfo.Name);
            Assert.Equal("delay-async", work.Arguments[0]);
        }

        [Fact]
        public async Task 定时任务_状态读回后_延时信息不丢失()
        {
            var workId = await Manager.Schedule(() => TestJobs.StaticWork(), TimeSpan.FromSeconds(30), "定时任务-状态读回");

            // 延时投递与失败重试都依赖"读回最后状态"，读回失败会让定时任务永远无法入队
            var state = await Query.GetLastState(workId);
            var scheduled = Assert.IsType<ScheduledWorkState>(state);
            Assert.Equal(TimeSpan.FromSeconds(30), scheduled.DelayTime);
        }

        #endregion

        #region Awaiting

        [Fact]
        public async Task Awaiting_Workload重载_父任务未完成时保持awaiting()
        {
            var parentId = await Manager.Enqueue(() => TestJobs.StaticWork(), "父任务");
            var work = Workload.FromExpression(() => TestJobs.StaticWork());

            var childId = await Manager.Awaiting(work, parentId, "连续任务-负载");

            await AssertAwaiting(childId, "连续任务-负载", parentId);
        }

        [Fact]
        public async Task Awaiting_静态同步Action_父任务未完成时保持awaiting()
        {
            var parentId = await Manager.Enqueue(() => TestJobs.StaticWork(), "父任务");

            var childId = await Manager.Awaiting(() => TestJobs.StaticWork(), parentId, "连续任务-静态同步");

            await AssertAwaiting(childId, "连续任务-静态同步", parentId);
        }

        [Fact]
        public async Task Awaiting_实例同步ActionT_父任务未完成时保持awaiting()
        {
            var parentId = await Manager.Enqueue(() => TestJobs.StaticWork(), "父任务");

            var childId = await Manager.Awaiting<InstanceJobs>(j => j.Write("after-parent"),
                parentId, "连续任务-实例同步");

            await AssertAwaiting(childId, "连续任务-实例同步", parentId);

            var data = await Query.GetWorkData(childId);
            var work = Workload.Deserialize(data!.Content);
            Assert.Equal(nameof(InstanceJobs.Write), work.MethodInfo.Name);
            Assert.Equal("after-parent", work.Arguments[0]);
        }

        [Fact]
        public async Task Awaiting_静态异步FuncTask_父任务未完成时保持awaiting()
        {
            var parentId = await Manager.Enqueue(() => TestJobs.StaticWork(), "父任务");

            var childId = await Manager.Awaiting(() => TestJobs.StaticWorkAsync(), parentId, "连续任务-静态异步");

            await AssertAwaiting(childId, "连续任务-静态异步", parentId);
        }

        [Fact]
        public async Task Awaiting_实例异步FuncTTask_父任务未完成时保持awaiting()
        {
            var parentId = await Manager.Enqueue(() => TestJobs.StaticWork(), "父任务");

            var childId = await Manager.Awaiting<InstanceJobs>(j => j.WriteAsync("after-parent-async"),
                parentId, "连续任务-实例异步");

            await AssertAwaiting(childId, "连续任务-实例异步", parentId);

            var data = await Query.GetWorkData(childId);
            var work = Workload.Deserialize(data!.Content);
            Assert.Equal(nameof(InstanceJobs.WriteAsync), work.MethodInfo.Name);
        }

        [Fact]
        public async Task Awaiting_父任务已完成时_立即入队()
        {
            var parentId = await Manager.Enqueue(() => TestJobs.StaticWork(), "父任务");

            var parentData = await Query.GetWorkData(parentId);
            Assert.NotNull(parentData);
            await WorkFactory.ChangeState(parentId,
                new SucceededWorkState(null, 1, "test"), parentData!.StateName, parentData);

            var childId = await Manager.Awaiting(() => TestJobs.StaticWork(), parentId, "父已完成-连续任务");

            await AssertCreated(childId, "父已完成-连续任务", EnqueuedWorkState.Name);
            Assert.True(await QueueRowExists(childId), "父任务已完成，连续任务应立即入队");
        }

        [Fact]
        public async Task Awaiting_父任务不存在时_立即入队()
        {
            var childId = await Manager.Awaiting(() => TestJobs.StaticWork(), "not-exist-parent", "父不存在-连续任务");

            await AssertCreated(childId, "父不存在-连续任务", EnqueuedWorkState.Name);
            Assert.True(await QueueRowExists(childId));
        }

        [Fact]
        public async Task Awaiting_父任务id为空_抛异常()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => Manager.Awaiting(() => TestJobs.StaticWork(), string.Empty, "空父任务"));
        }

        #endregion

        #region IWorkManager 其余方法

        [Fact]
        public async Task ChangeState_按指定状态流转()
        {
            var workId = await Manager.Enqueue(() => TestJobs.StaticWork(), "待流转任务");

            var changed = await Manager.ChangeState(new WorkStateContext(workId,
                new SucceededWorkState(null, 1, "test"), null));

            Assert.True(changed);
            Assert.Equal(SucceededWorkState.Name, (await Query.GetLastState(workId))!.StateName);
        }

        [Fact]
        public async Task ChangeState_工作不存在_返回false()
        {
            var changed = await Manager.ChangeState(new WorkStateContext("not-exist-work-id",
                new SucceededWorkState(null, 1, "test"), null));

            Assert.False(changed);
        }

        [Fact]
        public async Task RemoveWork_删除工作与状态()
        {
            var workId = await Manager.Enqueue(() => TestJobs.StaticWork(), "待删除任务");
            Assert.True(await WorkRowExists(workId));

            await Manager.RemoveWork(workId);

            Assert.False(await WorkRowExists(workId));
            Assert.Null(await Query.GetLastState(workId));
        }

        [Fact]
        public async Task RemoveWork_workId为空_抛异常()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => Manager.RemoveWork(string.Empty));
        }

        [Fact]
        public async Task ReEnqueue_重新入队并发布消息()
        {
            var workId = await Manager.Enqueue(() => TestJobs.StaticWork(), "待重投任务");
            Assert.True(await QueueRowExists(workId));

            // 任务已完成且消息已出队后重新入队，应重新发布消息
            await Manager.ChangeState(new WorkStateContext(workId,
                new SucceededWorkState(null, 1, "test"), null));
            await RemoveQueueRow(workId);
            Assert.False(await QueueRowExists(workId));

            await Manager.ReEnqueue(workId);

            Assert.Equal(EnqueuedWorkState.Name, (await Query.GetLastState(workId))!.StateName);
            Assert.True(await QueueRowExists(workId), "重新入队应重新发布队列消息");
            Assert.Equal(Helper.DefaultQueue, await QueueNameOf(workId));
        }

        #endregion

        #region assert helpers

        /// <summary>校验工作已按预期名称与状态落库</summary>
        private async Task<WorkData> AssertCreated(string workId, string expectedName, string expectedStateName)
        {
            Assert.False(string.IsNullOrEmpty(workId), "创建应返回工作 Id");

            var data = await Query.GetWorkData(workId);
            Assert.NotNull(data);
            Assert.Equal(expectedName, data!.Name);
            Assert.Equal(expectedStateName, data.StateName);

            // 名称必须落库（监控面板直接读该列）
            Assert.Equal(expectedName, await WorkNameInDb(workId));

            return data;
        }

        private async Task AssertScheduled(string workId, string expectedName)
        {
            await AssertCreated(workId, expectedName, ScheduledWorkState.Name);
            Assert.False(await QueueRowExists(workId), "定时任务创建时不应入队");
            Assert.True(await Query.CheckCacheSet(ScheduledWorkState.Name, workId), "定时任务应登记到调度缓存");
        }

        private async Task AssertAwaiting(string workId, string expectedName, string parentId)
        {
            await AssertCreated(workId, expectedName, AwaitingWorkState.Name);
            Assert.False(await QueueRowExists(workId), "父任务未完成时连续任务不应入队");
            Assert.True(await Query.CheckCacheSet($"{AwaitingWorkState.Name}:{parentId}", workId),
                "连续任务应登记在父任务名下，父任务完成后才触发");
        }

        #endregion
    }
}
