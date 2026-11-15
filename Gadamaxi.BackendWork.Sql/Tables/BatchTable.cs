using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Sql.DBMigrators;
using LinqToDB.Mapping;
using System;

namespace Gadamaxi.BackendWork.Sql.Tables
{
    [Table("Batch",Schema = Defaults.SchemaName)]
    public class BatchTable
    {
        [PrimaryKey]
        public string BatchId { get; set; } = string.Empty;
        [Column]
        public string Name { get; set; } = string.Empty;
        [Column]
        public string ParentBatchId { get; set; } = string.Empty;
        [Column]
        public BatchStatus Status { get; set; }
        [Column]
        public string Message { get; set; } = string.Empty;
        [Column]
        public int RetryCount { get; set; }
        [Column]
        public int Count { get; set; }
        [Column]
        public int DealCount { get; set; }
        [Column]
        public DateTime ExpireUtcTime { get; set; }
        [Column]
        public DateTime CreateUtcTime { get; set; }
        [Column]
        public DateTime? UpdateUtcTime { get; set; }
    }
}
