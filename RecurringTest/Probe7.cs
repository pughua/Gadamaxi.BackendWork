using Gadamaxi.BackendWork.Sql.Tables;
using LinqToDB;
using LinqToDB.Data;
using System;

namespace RecurringTest
{
    /// <summary>探针7：对照实验——仅连接串 DateTimeKind=Utc（无 MappingSchema）。</summary>
    internal static class Probe7
    {
        public static int Run()
        {
            var dbFile = System.IO.Path.Combine(AppContext.BaseDirectory, "recurring-test.db");
            var options = new DataOptions().UseSQLite($"Data Source={dbFile};Cache=Shared;DateTimeKind=Utc");
            using (var db = new DataConnection(options))
            {
                var row = db.GetTable<RecurringTable>().Where(r => r.Id == "latency-001").First();
                Console.WriteLine($"[仅连接串] CreateUtcTime = {row.CreateUtcTime:O} Kind={row.CreateUtcTime.Kind}");
            }
            return 0;
        }
    }
}
