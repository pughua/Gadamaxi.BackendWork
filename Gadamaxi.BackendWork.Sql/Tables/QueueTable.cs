using Gadamaxi.BackendWork.Sql.DBMigrators;
using LinqToDB.Mapping;
using System;

namespace Gadamaxi.BackendWork.Sql.Tables
{
    [Table("Queue", Schema = Defaults.SchemaName)]
    public class QueueTable
    {
        [PrimaryKey]
        public long Id { get; set; }
        [Column]
        public string WorkId { get; set; } = string.Empty;
        [Column]
        public string Queue { get; set; } = string.Empty;
        [Column]
        public DateTime? FetchUtcTime { get; set; }

        /// <summary>
        /// 抢占标记：出队（Subscribe）时写入本次抢占的唯一标记。
        /// 出队已去掉全局分布式锁（#3），改为"条件 UPDATE + 按标记回读"判定抢占是否成功，
        /// 因此该列既用于识别抢占者，也用于避免并发下同一消息被两个消费者同时取走。
        /// </summary>
        [Column]
        public string? FetchedBy { get; set; }
    }
}
