using Gadamaxi.BackendWork.Batchs;
using MongoDB.Bson.Serialization.Attributes;
using System;

namespace Gadamaxi.BackendWork.Mongodb.Documents
{
    /// <summary>
    /// 批量（对应 Sql 的 BatchTable）。状态收敛与 Sql 完全同口径：按批量项的实际结果收敛。
    /// </summary>
    public class BatchDocument
    {
        [BsonId]
        public string BatchId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string ParentBatchId { get; set; } = string.Empty;

        public BatchStatus Status { get; set; }

        public string Message { get; set; } = string.Empty;

        public int RetryCount { get; set; }

        public int Count { get; set; }

        public int DealCount { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime ExpireUtcTime { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime CreateUtcTime { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime? UpdateUtcTime { get; set; }
    }

    /// <summary>
    /// 批量项（对应 Sql 的 BatchItemTable）。
    /// WorkId 解绑（置空串）用于批量重试：让失败项能重新展开出新的子工作。
    /// </summary>
    public class BatchItemDocument
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string BatchId { get; set; } = string.Empty;

        public string? WorkId { get; set; } = string.Empty;

        public BatchStatus Status { get; set; }

        public string Message { get; set; } = string.Empty;

        public string WorkJson { get; set; } = string.Empty;

        public string DataJson { get; set; } = string.Empty;

        public string DataType { get; set; } = string.Empty;

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime CreateUtcTime { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
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
