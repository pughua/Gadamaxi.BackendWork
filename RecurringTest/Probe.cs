using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Recurrings;
using System;

namespace RecurringTest
{
    /// <summary>探针：逐环节打印周期任务时间计算链路的 Kind/ticks/缓存戳。</summary>
    internal static class Probe
    {
        public static int Run()
        {
            Console.WriteLine($"Machine TZ = {TimeZoneInfo.Local.Id}, offset={TimeZoneInfo.Local.BaseUtcOffset}");
            var now = Helper.CurrentUtcTime();
            Console.WriteLine($"now = {now:O} Kind={now.Kind}");

            // 复现 AddOrUpdate 创建路径：GenNextExecuteTime(Create-1s, Create)
            var create = now;
            var rw = new RecurringWork("probe")
            {
                Cron = "* * * * *",
                TimeZone = TimeZoneInfo.Local.Id,
                CreateUtcTime = create
            };
            var ret = rw.GenNextExecuteTime(create.AddSeconds(-1), create);
            PrintStage("setup: GenNextExecuteTime(Create-1s, Create)", rw.NextExecuteUtcTime);

            var setupScore = rw.NextExecuteUtcTime!.Value.UnixMillisecondStamp();
            Console.WriteLine($"  setup cache score = {setupScore} = {DateTimeOffset.FromUnixTimeMilliseconds(setupScore):O} (real-UTC frame)");
            Console.WriteLine($"  due in {(setupScore - now.UnixMillisecondStamp()) / 1000.0:F1}s");

            // 复现触发路径：GenNextExecuteTime(LastExecute=null→CreateUtcTime, now=+90s)
            Console.WriteLine();
            var now2 = create.AddSeconds(90);
            rw.LastExecuteUtcTime = null;
            rw.GenNextExecuteTime(rw.LastExecuteUtcTime ?? rw.CreateUtcTime, now2);
            PrintStage("trigger: GenNextExecuteTime(CreateUtcTime, now+90s)", rw.NextExecuteUtcTime);
            var trigScore = rw.NextExecuteUtcTime!.Value.UnixMillisecondStamp();
            Console.WriteLine($"  post-trigger cache score = {trigScore} = {DateTimeOffset.FromUnixTimeMilliseconds(trigScore):O} (real-UTC frame)");
            Console.WriteLine($"  due in {(trigScore - now2.UnixMillisecondStamp()) / 1000.0:F1}s  (应为下一分钟≈-30s，若为+8h 即根因)");

            // CheckCurrentTimeRun 用真实 now
            Console.WriteLine();
            var hit = rw.CheckCurrentTimeRun(now2.UnixMillisecondStamp());
            Console.WriteLine($"  CheckCurrentTimeRun(now+90s) = {hit}");

            // 模拟 linq2db 读回：字符串带 Z 后缀解析后的 Kind
            Console.WriteLine();
            var stored = $"2026-09-22 23:07:00Z";
            if (DateTime.TryParse(stored, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                Console.WriteLine($"parse('{stored}') = {parsed:O} Kind={parsed.Kind} -> stamp={parsed.UnixMillisecondStamp()}");
                Console.WriteLine($"  = {DateTimeOffset.FromUnixTimeMilliseconds(parsed.UnixMillisecondStamp()):O} (real-UTC frame)");
            }
            return 0;
        }

        private static void PrintStage(string stage, DateTime? dt)
        {
            if (dt == null) { Console.WriteLine($"{stage}: null"); return; }
            var v = dt.Value;
            Console.WriteLine($"{stage}: value={v:O} Kind={v.Kind} ticks={v.Ticks}");
            foreach (var occ in new System.Collections.Generic.List<DateTime>()) { _ = occ; }
        }
    }
}
