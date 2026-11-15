using Gadamaxi.BackendWork.Resources;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Sql.DBMigrators;
using Gadamaxi.BackendWork.Works.States;
using LinqToDB.Mapping;
using System;
using System.Collections.Generic;
namespace Gadamaxi.BackendWork.Sql.Tables
{
    [Table("State", Schema = Defaults.SchemaName)]
    public class StateTable
    {
        [PrimaryKey]
        public string Id { get; set; } = string.Empty;
        [Column]
        public string Name { get; set; } = string.Empty;
        [Column]
        public string WorkId { get; set; } = string.Empty;
        [Column]
        public string Info { get; set; } = string.Empty;
        [Column]
        public DateTime CreateUtcTime { get; set; }
        [Column]
        public string JsonData { get; set; } = string.Empty;

        public IWorkState ToState()
        {
            var data = Helper.Deserialize<Dictionary<string, string>>(JsonData);
            var state = new EmptyWorkState(Name,Info,CreateUtcTime, data);

            return WorkFactory.ConvertState(state);
        }
    }
}
