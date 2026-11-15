using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Works.States;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;

namespace Gadamaxi.BackendWork.Mongodb.Documents
{
    /// <summary>
    /// 工作行（对应 Sql 的 WorkTable）。主键即工作 Id。
    /// </summary>
    public class WorkDocument
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string StateId { get; set; } = string.Empty;

        public string StateName { get; set; } = string.Empty;

        public string InvocationJson { get; set; } = string.Empty;

        public string ParametersJson { get; set; } = string.Empty;

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime CreateUtcTime { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime? ExpireUtcTime { get; set; }
    }

    /// <summary>
    /// 状态行（对应 Sql 的 StateTable）。一个工作有多条状态变更历史，最后一条即当前状态。
    /// </summary>
    public class StateDocument
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string WorkId { get; set; } = string.Empty;

        public string Info { get; set; } = string.Empty;

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime CreateUtcTime { get; set; }

        public string JsonData { get; set; } = string.Empty;

        public IWorkState ToState()
        {
            var data = Helper.Deserialize<System.Collections.Generic.Dictionary<string, string>>(JsonData);
            var state = new EmptyWorkState(Name, Info, CreateUtcTime, data);

            return WorkFactory.ConvertState(state);
        }
    }

    /// <summary>
    /// 队列消息（对应 Sql 的 QueueTable）。
    /// <para>
    /// 主键用 ObjectId 而非自增长整型：ObjectId 本身按生成时刻递增，
    /// 天然给出"先进先出"的投递顺序，不需要额外的序号生成器。
    /// </para>
    /// </summary>
    public class QueueDocument
    {
        [BsonId]
        public ObjectId Id { get; set; }

        public string WorkId { get; set; } = string.Empty;

        public string Queue { get; set; } = string.Empty;

        /// <summary>本次抢占时刻。为空表示从未被取走。</summary>
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime? FetchUtcTime { get; set; }

        /// <summary>
        /// 抢占标记（对应 Sql 的 FetchedBy）：出队时写入本次抢占的唯一标记，
        /// 用于"条件更新 + 按标记回读"判定本次是否抢到，取代全局分布式锁。
        /// </summary>
        public string? FetchedBy { get; set; }
    }

    /// <summary>
    /// 调度缓存（对应 Sql 的 CacheSetTable）：按 Type 分桶、按 Score（毫秒时间戳）排序的到期索引。
    /// (Type, Value) 上建有唯一索引——重复登记会直接失败并回滚整个事务，
    /// 因此"重新登记"路径必须先删除再登记。
    /// </summary>
    public class CacheSetDocument
    {
        [BsonId]
        public ObjectId Id { get; set; }

        public string Type { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;

        public double Score { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime? ExpireUtcTime { get; set; }
    }
}
