using Gadamaxi.BackendWork.Sql.DBMigrators;
using LinqToDB.Mapping;
using System;

namespace Gadamaxi.BackendWork.Sql.Tables
{
    [Table("Lock",Schema = Defaults.SchemaName)]
    public class LockTable
    {
        [PrimaryKey]
        public string Key { get; set; } = string.Empty;
        [Column]
        public string Name { get; set; } = string.Empty;
        [Column]
        public DateTime AcquiredUtcTime { get; set; }
        [Column]
        public DateTime ExpireUtcTime { get; set; }
        [Column]
        public int Count { get; set; }
        /// <summary>
        /// 锁持有者标识。抢占、续期、释放都必须按它限定作用域，
        /// 否则锁过期被别的节点抢走后，原持有者仍能续期或删掉别人的锁。
        /// </summary>
        [Column]
        public string Owner { get; set; } = string.Empty;
    }
}
