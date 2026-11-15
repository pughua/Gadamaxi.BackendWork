using System;
using System.Linq;
using System.Threading.Tasks;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 周期任务防重触发（#11）：
    /// 触发路径在写工作前先以 CAS（基于 LastExecuteUtcTime 版本号）抢占触发权，
    /// 并发触发者只会有一个成功建工作，其余因 CAS 失败而抛异常回滚整个触发事务。
    /// </summary>
    public class RecurringDedupTests : BackendWorkTestBase
    {
        private const string EveryMinute = "* * * * *";

        private IRecurringWorkManager RecurringManager => Provider.GetRequiredService<IRecurringWorkManager>();

        private IWorkStateHandlerProvider HandlerProvider => Provider.GetRequiredService<IWorkStateHandlerProvider>();

        /// <summary>
        /// 验证 CAS 原语本身：版本匹配则推进；版本不匹配（已被抢先）则抛异常回滚、不覆盖。
        /// 做确定性断言，不依赖并发时序。
        /// </summary>
        [Fact]
        public async Task AdvanceRecurringWork_CAS_版本不匹配时抛异常并回滚()
        {
            await RecurringManager.AddOrUpdate("recurring-cas", () => TestJobs.StaticWork(),
                EveryMinute, Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "CAS测试");

            var recurring = await Query.GetRecurringWorkById("recurring-cas");
            Assert.NotNull(recurring);

            var now = Helper.CurrentUtcTime();

            // 期望版本为 null（初始）：CAS 应成功推进
            using (var s1 = Provider.GetRequiredService<IWorkStorage>())
            {
                await s1.AdvanceRecurringWork(Clone(recurring!, now), recurring!.LastExecuteUtcTime);
                await s1.Commit();
            }
            // 注意：SQLite 读回 DateTime 为本地时区帧，不能直接与 UTC 的 now 比较；用读回值自身做基线。
            var afterWin = await Query.GetRecurringWorkById("recurring-cas");
            Assert.NotNull(afterWin!.LastExecuteUtcTime);

            // 再次以旧版本（null）CAS：实际已推进，不匹配 → 抛异常、回滚、不覆盖
            using (var s2 = Provider.GetRequiredService<IWorkStorage>())
            {
                await s2.AdvanceRecurringWork(Clone(recurring, Helper.CurrentUtcTime().AddHours(1)), null);
                await Assert.ThrowsAsync<RecurringDuplicateTriggerException>(() => s2.Commit());
            }
            var afterLose = await Query.GetRecurringWorkById("recurring-cas");
            Assert.Equal(afterWin.LastExecuteUtcTime, afterLose!.LastExecuteUtcTime);
        }

        /// <summary>
        /// 模拟"两个触发者读取同一快照后并发推进"：两者都读到 LastExecuteUtcTime==null，
        /// 各自把 CAS 排在触发事务首位。先提交者抢占成功并建工作；后提交者 CAS 失败抛异常回滚，不建工作。
        /// 断言整库只产生一个工作（确定性，不依赖 Timer 时序）。
        /// </summary>
        [Fact]
        public async Task Trigger_并发触发_CAS抢占_只产生一个工作()
        {
            await RecurringManager.AddOrUpdate("recurring-race", () => TestJobs.StaticWork(),
                EveryMinute, Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "周期并发");

            using var storage1 = Provider.GetRequiredService<IWorkStorage>();
            using var storage2 = Provider.GetRequiredService<IWorkStorage>();

            // 两个触发者先后读取同一快照（此时库里 LastExecuteUtcTime 仍为 null），都尚未提交
            await WorkFactory.TriggerRecurringWork(storage1, Query, Queue, HandlerProvider, "recurring-race", GetType().Name);
            await WorkFactory.TriggerRecurringWork(storage2, Query, Queue, HandlerProvider, "recurring-race", GetType().Name);

            await storage1.Commit();
            await Assert.ThrowsAsync<RecurringDuplicateTriggerException>(() => storage2.Commit());

            Assert.Equal(1, await Db.CountWorksAsync());

            var recurring = await Query.GetRecurringWorkById("recurring-race");
            Assert.NotNull(recurring);
            Assert.False(string.IsNullOrEmpty(recurring!.LastWorkId));
            Assert.True(await WorkRowExists(recurring.LastWorkId));
            Assert.Equal(EnqueuedWorkState.Name, (await Query.GetWorkData(recurring.LastWorkId))!.StateName);
        }

        /// <summary>
        /// 正常单次触发仍应生成工作并更新 LastWorkId（回归：CAS 不应影响正常路径）。
        /// </summary>
        [Fact]
        public async Task Trigger_单次触发_CAS成功_生成工作()
        {
            await RecurringManager.AddOrUpdate("recurring-single", () => TestJobs.StaticWork(),
                EveryMinute, Helper.DefaultQueue, new RecurringWorkContext { TimeZoneInfo = TimeZoneInfo.Utc }, "周期单次");

            await RecurringManager.Trigger("recurring-single");

            var recurring = await Query.GetRecurringWorkById("recurring-single");
            Assert.NotNull(recurring);
            Assert.False(string.IsNullOrEmpty(recurring!.LastWorkId));
            Assert.NotNull(recurring.LastExecuteUtcTime);
            Assert.True(await WorkRowExists(recurring.LastWorkId));
        }

        private static RecurringWork Clone(RecurringWork src, DateTime lastExecut)
        {
            return new RecurringWork(src.Id)
            {
                Name = src.Name,
                Cron = src.Cron,
                WorkContent = src.WorkContent,
                Queue = src.Queue,
                TimeZone = src.TimeZone,
                NextExecuteUtcTime = src.NextExecuteUtcTime,
                LastExecuteUtcTime = lastExecut,
                LastWorkId = src.LastWorkId,
                RetryCount = src.RetryCount,
                ErrorInfo = src.ErrorInfo,
            };
        }
    }
}
