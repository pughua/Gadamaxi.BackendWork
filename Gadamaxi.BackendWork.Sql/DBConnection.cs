using Gadamaxi.BackendWork.Sql.Tables;
using LinqToDB;
using System;

namespace Gadamaxi.BackendWork.Sql
{
    public class DBConnection: LinqToDB.Data.DataConnection
    {
        public DBConnection(DataOptions options) : base(options)
        {
        }

        public ITable<CacheSetTable> CacheSetTable => this.GetTable<CacheSetTable>();
        public ITable<LockTable> LockTable => this.GetTable<LockTable>();
        public ITable<QueueTable> QueueTable => this.GetTable<QueueTable>();
        public ITable<RecurringTable> RecurringTable => this.GetTable<RecurringTable>();
        public ITable<ServerTable> ServerTable => this.GetTable<ServerTable>();
        public ITable<StateTable> StateTable => this.GetTable<StateTable>();
        public ITable<WorkTable> WorkTable => this.GetTable<WorkTable>();

        public ITable<BatchTable> BatchTable => this.GetTable<BatchTable>();
        public ITable<BatchItemTable> BatchItemTable => this.GetTable<BatchItemTable>();

        public ITable<SagaTable> SagaTable => this.GetTable<SagaTable>();
        public ITable<SagaStepTable> SagaStepTable => this.GetTable<SagaStepTable>();
    }
}
