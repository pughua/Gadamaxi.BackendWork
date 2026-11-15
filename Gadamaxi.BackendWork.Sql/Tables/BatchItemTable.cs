using Gadamaxi.BackendWork.Batchs;
using Gadamaxi.BackendWork.Sql.DBMigrators;
using LinqToDB.Mapping;
using System;

namespace Gadamaxi.BackendWork.Sql.Tables
{
    [Table("BatchItem", Schema=Defaults.SchemaName)]
    public class BatchItemTable
    {
        [PrimaryKey]
        public string Id { get; set; } = string.Empty;
        [Column]
        public string Name { get; set; } = string.Empty;
        [Column]
        public string BatchId { get; set; } = string.Empty;
        [Column]
        public string? WorkId { get; set; } = string.Empty;
        [Column]
        public BatchStatus Status { get; set; }
        [Column]
        public string Message { get; set; } = string.Empty;
        [Column]
        public string WorkJson { get; set; } = string.Empty;
        [Column]
        public string DataJson { get; set; } = string.Empty;
        [Column]
        public string DataType { get; set; } = string.Empty;
        [Column]
        public DateTime CreateUtcTime { get; set; }
        [Column]
        public DateTime? UpdateUtcTime { get; set; }

        public BatchItem ToItem()
        {
            var data = (BatchItemData)Helper.Deserialize(DataJson, Helper.GetType(DataType));
            var item = new BatchItem
            {
                Id = Id,
                Name = Name,
                Status = Status,
                Message = Message,
                Work = Workload.Deserialize(WorkJson),
                WorkId = WorkId,
                Data = data
            };
            return item;
        }
    }
}
