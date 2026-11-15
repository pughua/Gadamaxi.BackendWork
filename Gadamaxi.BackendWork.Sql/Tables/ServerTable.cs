using Gadamaxi.BackendWork.Sql.DBMigrators;
using LinqToDB.Mapping;
using System;

namespace Gadamaxi.BackendWork.Sql.Tables
{
    [Table("Server", Schema = Defaults.SchemaName)]
    public class ServerTable
    {
        [PrimaryKey]
        public string ServerName { get; set; } = string.Empty;
        
        [Column]
        public DateTime CreateUtcTime { get; set; }
        [Column]
        public DateTime LastHeartbeatUtcTime { get;set; }
        [Column]
        public string JsonData { get; set; } = string.Empty;
    }
}
