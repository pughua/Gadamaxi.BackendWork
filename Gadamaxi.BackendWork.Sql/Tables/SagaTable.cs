using Gadamaxi.BackendWork.Sagas;
using Gadamaxi.BackendWork.Sql.DBMigrators;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;

namespace Gadamaxi.BackendWork.Sql.Tables
{
    [Table("Saga", Schema = Defaults.SchemaName)]
    public class SagaTable
    {
        [PrimaryKey]
        public string SagaId { get; set; } = string.Empty;
        [Column]
        public string Name { get; set; } = string.Empty;
        [Column]
        public string CorrelationId { get; set; } = string.Empty;
        [Column]
        public string SagaType { get; set; } = string.Empty;
        [Column]
        public string InputJson { get; set; } = string.Empty;
        [Column]
        public SagaStatus Status { get; set; }
        [Column]
        public int StepCount { get; set; }
        [Column]
        public int CurrentIndex { get; set; }
        [Column]
        public int CompensationIndex { get; set; }
        [Column]
        public string VariablesJson { get; set; } = string.Empty;
        [Column]
        public string Message { get; set; } = string.Empty;
        [Column]
        public DateTime ExpireUtcTime { get; set; }
        [Column]
        public DateTime CreateUtcTime { get; set; }
        [Column]
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
}
