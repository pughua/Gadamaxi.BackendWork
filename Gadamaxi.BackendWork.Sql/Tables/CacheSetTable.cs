using Gadamaxi.BackendWork.Sql.DBMigrators;
using LinqToDB.Mapping;
using System;

namespace Gadamaxi.BackendWork.Sql.Tables
{
    [Table("CacheSet", Schema = Defaults.SchemaName)]
    public class CacheSetTable
    {
        [PrimaryKey]
        public long Id { get; set; }
        [Column]
        public string Type { get; set; } = string.Empty;

        [Column]
        public string Value { get; set; } = string.Empty;
        [Column]
        public double Score { get; set; }
        [Column]
        public DateTime? ExpireUtcTime { get; set; }
    }
}
