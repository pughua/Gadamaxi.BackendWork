using Gadamaxi.BackendWork.Sql.DBMigrators;
using LinqToDB.Mapping;
using System;

namespace Gadamaxi.BackendWork.Sql.Tables
{
    [Table("Work", Schema = Defaults.SchemaName)]
    public class WorkTable
    {
        [PrimaryKey]
        public string Id { get; set; } = string.Empty;
        [Column]
        public string Name { get; set; } = string.Empty;
        [Column]
        public string StateId { get; set; } = string.Empty;
        [Column]
        public string StateName { get; set; } = string.Empty;
        [Column]
        public string InvocationJson { get; set; } = string.Empty;
        [Column]
        public string ParametersJson { get; set; } = string.Empty;
        [Column]
        public DateTime CreateUtcTime { get; set; }
        [Column]
        public DateTime? ExpireUtcTime { get; set; }
    }
}
