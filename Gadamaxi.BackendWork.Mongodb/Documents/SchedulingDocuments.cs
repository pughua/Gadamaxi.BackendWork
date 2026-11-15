using MongoDB.Bson.Serialization.Attributes;
using System;

namespace Gadamaxi.BackendWork.Mongodb.Documents
{
    /// <summary>
    /// 周期任务定义（对应 Sql 的 RecurringTable）。
    /// LastExecutUtcTime 同时充当"本次触发"的版本号：CAS 推进它即可防重复触发。
    /// </summary>
    public class RecurringDocument
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Cron { get; set; } = string.Empty;

        public string JsonContent { get; set; } = string.Empty;

        public string Queue { get; set; } = string.Empty;

        public string TimeZone { get; set; } = string.Empty;

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime CreateUtcTime { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime? NextExecutUtcTime { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime? LastExecutUtcTime { get; set; }

        public string LastWorkId { get; set; } = string.Empty;

        public int RetryCount { get; set; }

        public string ErrorInfo { get; set; } = string.Empty;

        /// <summary>暂停标记：为 true 时调度触发被跳过（缓存戳照常前进）。</summary>
        public bool Paused { get; set; }
    }

    /// <summary>
    /// 服务节点心跳（对应 Sql 的 ServerTable）。主键是节点名，注册时先删同名残留再插入。
    /// </summary>
    public class ServerDocument
    {
        [BsonId]
        public string ServerName { get; set; } = string.Empty;

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime CreateUtcTime { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime LastHeartbeatUtcTime { get; set; }

        public string JsonData { get; set; } = string.Empty;
    }

    /// <summary>
    /// 分布式锁（对应 Sql 的 LockTable）。主键即锁键，ExpireUtcTime 过期后允许被抢占。
    /// <para>
    /// 比 Sql 多一个 <see cref="Owner"/> 字段：锁过期被别人抢走后，原持有者的释放动作
    /// 必须不再生效，否则会把别人的锁删掉。释放时按 (Key, Owner) 精确匹配。
    /// </para>
    /// </summary>
    public class LockDocument
    {
        [BsonId]
        public string Key { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>当前持有者的唯一标记。释放时用它确认"还属于自己的锁"。</summary>
        public string Owner { get; set; } = string.Empty;

        public int Count { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime AcquiredUtcTime { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime ExpireUtcTime { get; set; }
    }
}
