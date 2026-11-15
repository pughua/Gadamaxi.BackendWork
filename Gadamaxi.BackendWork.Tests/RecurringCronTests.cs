using System;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Recurrings;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 验证 <see cref="RecurringWork.CheckCurrentTimeRun"/> 的 cron 匹配（#12）：
    /// 改用真正的 Cronos 解析器后，应支持步长/范围，并走真实时区（含 DST），
    /// 不再依赖手写字符串匹配与 BaseUtcOffset。
    /// </summary>
    public class RecurringCronTests
    {
        private static long Ms(DateTime utc) => utc.UnixMillisecondStamp();

        [Fact]
        public void CheckCurrentTimeRun_精确值_命中与未命中()
        {
            var work = new RecurringWork("x") { Cron = "0 9 * * *", TimeZone = TimeZoneInfo.Utc.Id };

            // 09:00 UTC 命中
            Assert.True(work.CheckCurrentTimeRun(Ms(new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc))));
            // 09:01 UTC 未命中
            Assert.False(work.CheckCurrentTimeRun(Ms(new DateTime(2026, 7, 1, 9, 1, 0, DateTimeKind.Utc))));
            // 10:00 UTC 未命中
            Assert.False(work.CheckCurrentTimeRun(Ms(new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc))));
        }

        [Fact]
        public void CheckCurrentTimeRun_步长_每15分钟()
        {
            var work = new RecurringWork("x") { Cron = "*/15 * * * *", TimeZone = TimeZoneInfo.Utc.Id };

            Assert.True(work.CheckCurrentTimeRun(Ms(new DateTime(2026, 7,1, 0, 0, 0, DateTimeKind.Utc))));
            Assert.True(work.CheckCurrentTimeRun(Ms(new DateTime(2026, 7,1, 0, 15, 0, DateTimeKind.Utc))));
            Assert.True(work.CheckCurrentTimeRun(Ms(new DateTime(2026, 7,1, 0, 30, 0, DateTimeKind.Utc))));
            Assert.True(work.CheckCurrentTimeRun(Ms(new DateTime(2026, 7,1, 0, 45, 0, DateTimeKind.Utc))));
            // 第 10 分钟不是 15 的倍数
            Assert.False(work.CheckCurrentTimeRun(Ms(new DateTime(2026, 7,1, 0, 10, 0, DateTimeKind.Utc))));
        }

        [Fact]
        public void CheckCurrentTimeRun_范围_小时区间()
        {
            var work = new RecurringWork("x") { Cron = "0 9-17 * * *", TimeZone = TimeZoneInfo.Utc.Id };

            Assert.True(work.CheckCurrentTimeRun(Ms(new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc))));
            Assert.True(work.CheckCurrentTimeRun(Ms(new DateTime(2026, 7, 1, 17, 0, 0, DateTimeKind.Utc))));
            // 18:00 超出范围
            Assert.False(work.CheckCurrentTimeRun(Ms(new DateTime(2026, 7, 1, 18, 0, 0, DateTimeKind.Utc))));
        }

        [Fact]
        public void CheckCurrentTimeRun_夏令时_走真实时区而非BaseUtcOffset()
        {
            // Windows 时区 ID；非 Windows 跳过（不影响其它用例）
            TimeZoneInfo tz;
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            catch
            {
                return;
            }

            // 夏季（EDT，UTC-4）：当地 12:00 = 16:00 UTC
            var summer = new RecurringWork("x") { Cron = "0 12 * * *", TimeZone = tz.Id };
            Assert.True(summer.CheckCurrentTimeRun(Ms(new DateTime(2026, 7, 1, 16, 0, 0, DateTimeKind.Utc))));
            Assert.False(summer.CheckCurrentTimeRun(Ms(new DateTime(2026, 7, 1, 17, 0, 0, DateTimeKind.Utc))));

            // 冬季（EST，UTC-5）：当地 12:00 = 17:00 UTC
            var winter = new RecurringWork("x") { Cron = "0 12 * * *", TimeZone = tz.Id };
            Assert.True(winter.CheckCurrentTimeRun(Ms(new DateTime(2026, 1, 1, 17, 0, 0, DateTimeKind.Utc))));
            Assert.False(winter.CheckCurrentTimeRun(Ms(new DateTime(2026, 1, 1, 16, 0, 0, DateTimeKind.Utc))));
        }

        [Fact]
        public void GenNextExecuteTime_永不命中的cron_不抛异常()
        {
            // 2 月 30 日永不命中：旧实现会在 GetNextOccurrence 返回 null 时 NRE/死循环
            var work = new RecurringWork("x")
            {
                Cron = "0 0 30 2 *",
                TimeZone = TimeZoneInfo.Utc.Id,
                CreateUtcTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };

            var ex = Record.Exception(() =>
                work.GenNextExecuteTime(work.CreateUtcTime,
                    new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));

            Assert.Null(ex);
        }
    }
}
