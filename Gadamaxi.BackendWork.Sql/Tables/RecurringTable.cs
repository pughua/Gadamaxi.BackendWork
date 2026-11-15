using Gadamaxi.BackendWork.Sql.DBMigrators;
using LinqToDB.Mapping;
using System;

namespace Gadamaxi.BackendWork.Sql.Tables
{
    [Table("Recurring", Schema = Defaults.SchemaName)]
    public class RecurringTable
    {
        [PrimaryKey]
        public string Id { get; set; } = string.Empty;
        [Column]
        public string Name { get; set; } = string.Empty;
        [Column]
        public string Cron { get; set; } = string.Empty;
        [Column]
        public string JsonContent { get; set; } = string.Empty;
        [Column]
        public string Queue { get; set; } = string.Empty;
        [Column]
        public string TimeZone { get; set; } = string.Empty;
        [Column]
        public DateTime CreateUtcTime { get; set; }
        [Column]
        public DateTime? NextExecutUtcTime { get; set; }
        [Column]
        public DateTime? LastExecutUtcTime { get; set; }
        [Column]
        public string LastWorkId { get; set; } = string.Empty;
        [Column]
        public int RetryCount { get; set; }
        [Column]
        public string ErrorInfo { get; set; } = string.Empty;
        [Column]
        public bool Paused { get; set; }
    }
}
