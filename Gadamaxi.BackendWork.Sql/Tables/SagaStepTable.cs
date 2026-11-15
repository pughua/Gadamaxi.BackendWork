using Gadamaxi.BackendWork.Sagas;
using Gadamaxi.BackendWork.Sql.DBMigrators;
using LinqToDB.Mapping;
using System;

namespace Gadamaxi.BackendWork.Sql.Tables
{
    [Table("SagaStep", Schema = Defaults.SchemaName)]
    public class SagaStepTable
    {
        [PrimaryKey]
        public string StepId { get; set; } = string.Empty;
        [Column]
        public string SagaId { get; set; } = string.Empty;
        [Column]
        public int Index { get; set; }
        [Column]
        public string Name { get; set; } = string.Empty;
        [Column]
        public string ForwardMethod { get; set; } = string.Empty;
        [Column]
        public string CompensateMethod { get; set; } = string.Empty;
        [Column]
        public string OutputKey { get; set; } = string.Empty;
        [Column]
        public SagaStepStatus Status { get; set; }
        [Column]
        public string Message { get; set; } = string.Empty;
        [Column]
        public string ForwardWorkId { get; set; } = string.Empty;
        [Column]
        public string CompensateWorkId { get; set; } = string.Empty;
        [Column]
        public DateTime CreateUtcTime { get; set; }
        [Column]
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
