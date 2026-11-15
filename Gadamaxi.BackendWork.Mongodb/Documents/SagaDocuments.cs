using Gadamaxi.BackendWork.Sagas;
using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;

namespace Gadamaxi.BackendWork.Mongodb.Documents
{
    /// <summary>
    /// saga 实例（对应 Sql 的 SagaTable）。
    /// SagaType + InputJson 支撑执行期实例还原；两个游标（CurrentIndex / CompensationIndex）串行推进。
    /// </summary>
    public class SagaDocument
    {
        [BsonId]
        public string SagaId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>业务关联键，唯一索引：同一业务重复启动返回既有实例（幂等创建）。</summary>
        public string CorrelationId { get; set; } = string.Empty;

        public string SagaType { get; set; } = string.Empty;

        public string InputJson { get; set; } = string.Empty;

        public SagaStatus Status { get; set; }

        public int StepCount { get; set; }

        public int CurrentIndex { get; set; }

        public int CompensationIndex { get; set; }

        public string VariablesJson { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime ExpireUtcTime { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime CreateUtcTime { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime? UpdateUtcTime { get; set; }

        public SagaInstance ToSaga()
        {
            return new SagaInstance
            {
                SagaId = SagaId,
                Name = Name,
                CorrelationId = CorrelationId,
                SagaType = SagaType,
                InputJson = InputJson,
                Status = Status,
                StepCount = StepCount,
                CurrentIndex = CurrentIndex,
                CompensationIndex = CompensationIndex,
                Variables = Helper.Deserialize<Dictionary<string, string>>(VariablesJson) ?? new Dictionary<string, string>(),
                Message = Message,
                ExpireUtcTime = ExpireUtcTime,
                CreateUtcTime = CreateUtcTime,
                UpdateUtcTime = UpdateUtcTime
            };
        }
    }

    /// <summary>
    /// saga 步骤（对应 Sql 的 SagaStepTable）。只存方法名，执行期由 SagaStepRunner 还原实例后按名调用。
    /// </summary>
    public class SagaStepDocument
    {
        [BsonId]
        public string StepId { get; set; } = string.Empty;

        public string SagaId { get; set; } = string.Empty;

        public int Index { get; set; }

        public string Name { get; set; } = string.Empty;

        public string ForwardMethod { get; set; } = string.Empty;

        public string CompensateMethod { get; set; } = string.Empty;

        public string OutputKey { get; set; } = string.Empty;

        public SagaStepStatus Status { get; set; }

        public string Message { get; set; } = string.Empty;

        public string ForwardWorkId { get; set; } = string.Empty;

        public string CompensateWorkId { get; set; } = string.Empty;

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime CreateUtcTime { get; set; }

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime? UpdateUtcTime { get; set; }

        public SagaStep ToStep()
        {
            return new SagaStep
            {
                StepId = StepId,
                SagaId = SagaId,
                Index = Index,
                Name = Name,
                ForwardMethod = ForwardMethod,
                CompensateMethod = CompensateMethod,
                OutputKey = OutputKey,
                Status = Status,
                Message = Message,
                ForwardWorkId = ForwardWorkId,
                CompensateWorkId = CompensateWorkId,
                CreateUtcTime = CreateUtcTime,
                UpdateUtcTime = UpdateUtcTime
            };
        }
    }
}
