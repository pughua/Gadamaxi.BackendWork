using System;
using System.Collections.Generic;
using System.Linq;

namespace Gadamaxi.BackendWork.Recurrings
{
    public class RecurringWork
    {
        public RecurringWork(string id)
        {
            Id = id;
            CreateUtcTime = Helper.CurrentUtcTime();
            TimeZone = TimeZoneInfo.Utc.Id;
        }

        public string Id { get; private set; }
        public string Name { get; set; } = string.Empty;
        public string Cron { get; set; } = string.Empty;
        public string WorkContent { get; set; } = string.Empty;
        public string Queue { get; set; } = string.Empty;

        public string TimeZone { get; set; } = string.Empty;

        public DateTime CreateUtcTime { get; set; }
        public DateTime? NextExecuteUtcTime { get; set; }
        public DateTime? LastExecuteUtcTime { get; set; }
        public string LastWorkId { get; set; } = string.Empty;
        public int RetryCount { get; set; }

        public string ErrorInfo { get; set; } = string.Empty;

        /// <summary>
        /// 暂停标记：为 true 时调度触发被跳过（缓存戳照常前进，恢复后自然续跑）。
        /// 由监控面板的暂停/恢复操作维护，AddOrUpdate 更新定义时不重置该标记。
        /// </summary>
        public bool Paused { get; set; }

        internal void Update(Workload work, string cornExpression, RecurringWorkContext option,string name)
        {
            Cron = cornExpression;
            Name = name;
            Queue = work.Queue;
            WorkContent = work.Serialize();
            TimeZone = option.TimeZoneInfo.Id;
            if (CreateUtcTime != null)
            {
                CreateUtcTime = Helper.CurrentUtcTime();
            }
        }

        /// <summary>
        /// 计算 [start, end] 区间内 cron 的命中时刻，并把区间后的首个命中写入 <see cref="NextExecuteUtcTime"/>。
        /// </summary>
        public IEnumerable<DateTime> GenNextExecuteTime(DateTime start,DateTime end,bool immediately=false)
        {
            var resultList = new List<DateTime>();
            var timezone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
            var cron = Helper.ParseCron(Cron);

            var s = new DateTime(start.Ticks, DateTimeKind.Utc);
            var e = new DateTime(end.Ticks, DateTimeKind.Utc);
            DateTime? nexttime = s;
            while (true)
            {
                var occ = cron.GetNextOccurrence(nexttime.Value, timezone);
                if (occ == null)
                {
                    // cron 永不命中（如 2 月 30 日）：旧实现会在此 NRE/死循环，现安全退出
                    break;
                }

                if (occ.Value > e)
                {
                    // 首个命中已越过窗口，保留它作为下次轮询的缓存时间（与旧语义一致）
                    nexttime = occ;
                    break;
                }

                nexttime = occ;

                if (immediately)
                {
                    nexttime = e;
                    resultList.Add(nexttime.Value);
                    break;
                }

                resultList.Add(nexttime.Value);
                if (nexttime.Value == e)
                {
                    break;
                }
            }

            NextExecuteUtcTime = nexttime ?? s;

            return resultList;
        }

        public bool CheckCurrentTimeRun(long millisecond)
        {
            Helper.CheckArgumentNull(TimeZone, nameof(TimeZone));
            var timezone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
            Helper.CheckArgumentNull(timezone, nameof(timezone));

            // 改用真正的 cron 解析器（Cronos）判定是否命中当前分钟，取代原先的手写字符串匹配：
            // 1) 支持步长/范围/L/W 等全部 cron 语法（旧实现只支持精确值、遇到步长/范围即误判）；
            // 2) 经 TimeZoneInfo 走真实时区（含 DST），修复旧实现用 BaseUtcOffset 在夏令时区的时刻偏差。
            var nowUtc = DateTimeOffset.FromUnixTimeMilliseconds(millisecond).UtcDateTime;
            var minuteStartUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);
            var cron = Helper.ParseCron(Cron);
            var occ = cron.GetNextOccurrence(minuteStartUtc.AddMinutes(-1), timezone);
            return occ.HasValue && occ.Value.ToUniversalTime() == minuteStartUtc.ToUniversalTime();
        }
    }
}
