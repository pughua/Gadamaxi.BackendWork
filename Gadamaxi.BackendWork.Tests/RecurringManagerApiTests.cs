using System;
using System.Linq;
using System.Threading.Tasks;
using Cronos;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Works.States;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// IRecurringWorkManager.AddOrUpdate（接口方法 + 4 个扩展方法）与 Trigger / RemoveIfExists 验证。
    /// 断言落在"注册结果"上：定义落库、更新语义、队列回退、调度缓存登记、触发后生成工作。
    /// </summary>
    public class RecurringManagerApiTests : BackendWorkTestBase
    {
        private const string EveryMinute = "* * * * *";

        private IRecurringWorkManager RecurringManager => Provider.GetRequiredService<IRecurringWorkManager>();

        [Fact]
        public async Task AddOrUpdate_接口方法_落库并登记调度缓存()
        {
            var work = Workload.FromExpression(() => TestJobs.StaticWork(), Helper.DefaultQueue);

            await RecurringManager.AddOrUpdate("recurring-api", work, EveryMinute,
                new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "周期API测试");

            var recurring = await Query.GetRecurringWorkById("recurring-api");
            Assert.NotNull(recurring);
            Assert.Equal("周期API测试", recurring!.Name);
            Assert.Equal(EveryMinute, recurring.Cron);
            Assert.Equal(Helper.DefaultQueue, recurring.Queue);
            Assert.Equal(TimeZoneInfo.Utc.Id, recurring.TimeZone);
            Assert.NotNull(recurring.NextExecuteUtcTime);

            // 登记到周期调度缓存，RecurringWorkProcess 据此拉取
            Assert.True(await Query.CheckCacheSet(Helper.RecurringKey, "recurring-api"));
        }

        [Fact]
        public async Task AddOrUpdate_同一id重复调用_更新而不是新增()
        {
            await RecurringManager.AddOrUpdate("recurring-update", () => TestJobs.StaticWork(),
                EveryMinute, Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "原名");

            await RecurringManager.AddOrUpdate("recurring-update", () => TestJobs.StaticWorkAsync(),
                "*/5 * * * *", Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "新名");

            Assert.Equal(1, await Db.CountRecurringsAsync());

            var recurring = await Query.GetRecurringWorkById("recurring-update");
            Assert.NotNull(recurring);
            Assert.Equal("新名", recurring!.Name);
            Assert.Equal("*/5 * * * *", recurring.Cron);
        }

        [Fact]
        public async Task AddOrUpdate_静态异步FuncTask扩展方法_方法名落库正确()
        {
            await RecurringManager.AddOrUpdate("recurring-static-async", () => TestJobs.StaticWorkAsync(),
                EveryMinute, Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "周期-静态异步");

            var recurring = await Query.GetRecurringWorkById("recurring-static-async");
            Assert.NotNull(recurring);

            var work = Workload.Deserialize(recurring!.WorkContent);
            Assert.Equal(nameof(TestJobs.StaticWorkAsync), work.MethodInfo.Name);
            Assert.Equal(typeof(TestJobs), work.Type);
        }

        [Fact]
        public async Task AddOrUpdate_实例异步FuncTTask扩展方法_类型与方法名落库正确()
        {
            await RecurringManager.AddOrUpdate<InstanceJobs>("recurring-instance-async",
                j => j.WriteAsync("recurring"),
                EveryMinute, Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "周期-实例异步");

            var recurring = await Query.GetRecurringWorkById("recurring-instance-async");
            Assert.NotNull(recurring);

            var work = Workload.Deserialize(recurring!.WorkContent);
            Assert.Equal(typeof(InstanceJobs), work.Type);
            Assert.Equal(nameof(InstanceJobs.WriteAsync), work.MethodInfo.Name);
            Assert.Equal("recurring", work.Arguments[0]);
        }

        [Fact]
        public async Task AddOrUpdate_静态同步Action扩展方法_方法名落库正确()
        {
            await RecurringManager.AddOrUpdate("recurring-static-sync", () => TestJobs.StaticWork(),
                EveryMinute, Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "周期-静态同步");

            var recurring = await Query.GetRecurringWorkById("recurring-static-sync");
            Assert.NotNull(recurring);

            var work = Workload.Deserialize(recurring!.WorkContent);
            Assert.Equal(nameof(TestJobs.StaticWork), work.MethodInfo.Name);
        }

        [Fact]
        public async Task AddOrUpdate_实例同步ActionT扩展方法_类型与方法名落库正确()
        {
            await RecurringManager.AddOrUpdate<InstanceJobs>("recurring-instance-sync",
                j => j.Write("recurring-sync"),
                EveryMinute, Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "周期-实例同步");

            var recurring = await Query.GetRecurringWorkById("recurring-instance-sync");
            Assert.NotNull(recurring);

            var work = Workload.Deserialize(recurring!.WorkContent);
            Assert.Equal(typeof(InstanceJobs), work.Type);
            Assert.Equal(nameof(InstanceJobs.Write), work.MethodInfo.Name);
            Assert.Equal("recurring-sync", work.Arguments[0]);
        }

        [Fact]
        public async Task AddOrUpdate_队列为空_回退默认队列()
        {
            await RecurringManager.AddOrUpdate("recurring-default-queue", () => TestJobs.StaticWork(),
                EveryMinute, string.Empty, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "周期-默认队列");

            var recurring = await Query.GetRecurringWorkById("recurring-default-queue");
            Assert.NotNull(recurring);
            Assert.Equal(Helper.DefaultQueue, recurring!.Queue);
        }

        [Fact]
        public async Task Trigger_生成工作并入队_名称继承周期任务名()
        {
            await RecurringManager.AddOrUpdate("recurring-trigger", () => TestJobs.StaticWork(),
                EveryMinute, Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "周期触发");

            await RecurringManager.Trigger("recurring-trigger");

            var recurring = await Query.GetRecurringWorkById("recurring-trigger");
            Assert.NotNull(recurring);
            Assert.False(string.IsNullOrEmpty(recurring!.LastWorkId));
            Assert.NotNull(recurring.LastExecuteUtcTime);

            var data = await Query.GetWorkData(recurring.LastWorkId);
            Assert.NotNull(data);
            Assert.Equal(EnqueuedWorkState.Name, data!.StateName);
            Assert.True(await QueueRowExists(recurring.LastWorkId));
            Assert.Equal(Helper.DefaultQueue, await QueueNameOf(recurring.LastWorkId));
        }

        [Fact]
        public async Task Trigger_周期任务不存在_不抛异常()
        {
            await RecurringManager.Trigger("not-exist-recurring");
        }

        [Fact]
        public async Task RemoveIfExists_删除定义与调度缓存()
        {
            await RecurringManager.AddOrUpdate("recurring-remove", () => TestJobs.StaticWork(),
                EveryMinute, Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "周期删除");

            await RecurringManager.RemoveIfExists("recurring-remove");

            Assert.Null(await Query.GetRecurringWorkById("recurring-remove"));
            Assert.False(await Query.CheckCacheSet(Helper.RecurringKey, "recurring-remove"));
        }

        [Fact]
        public async Task AddOrUpdate_空名_抛异常()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => RecurringManager.AddOrUpdate("recurring-empty-name", () => TestJobs.StaticWork(),
                    EveryMinute, Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, string.Empty));
        }

        [Fact]
        public async Task AddOrUpdate_超长名_抛异常()
        {
            var tooLong = new string('x', Helper.MaxWorkNameLength + 1);

            await Assert.ThrowsAsync<ArgumentException>(
                () => RecurringManager.AddOrUpdate("recurring-long-name", () => TestJobs.StaticWork(),
                    EveryMinute, Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, tooLong));
        }

        [Fact]
        public async Task AddOrUpdate_非法cron_抛异常()
        {
            await Assert.ThrowsAsync<CronFormatException>(
                () => RecurringManager.AddOrUpdate("recurring-bad-cron", () => TestJobs.StaticWork(),
                    "not-a-cron", Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "周期-非法表达式"));
        }

        [Fact]
        public async Task AddOrUpdate_标识为空_抛异常()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => RecurringManager.AddOrUpdate(string.Empty, () => TestJobs.StaticWork(),
                    EveryMinute, Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "周期-空标识"));
        }
    }
}
