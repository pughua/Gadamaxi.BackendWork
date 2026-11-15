using FluentMigrator;
using Gadamaxi.BackendWork.Sql.Tables;
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Text;

namespace Gadamaxi.BackendWork.Sql.DBMigrators
{
    [Migration(1000, "InitTable")]
    public class InitTable : SchemaMigration
    {
        public override void Down()
        {
        }

        public override void Up()
        {
            if (!IsSqlite)
            {
                Create.Schema(SchemaName);
            }

            #region CacheSetTable
            CreateTable(nameof(CacheSetTable))
                .WithColumn(nameof(CacheSetTable.Id)).AsInt64().PrimaryKey().Identity()
                .WithColumn(nameof(CacheSetTable.Type)).AsString().NotNullable()
                .WithColumn(nameof(CacheSetTable.Value)).AsString().NotNullable()
                .WithColumn(nameof(CacheSetTable.Score)).AsDouble().NotNullable()
                .WithColumn(nameof(CacheSetTable.ExpireUtcTime)).AsDateTime().Nullable();

            Create.UniqueConstraint("idx_cachesettable_type_value")
                .OnTable(SubTableName(nameof(CacheSetTable)))
                .WithSchema(SchemaName)
                .Columns(nameof(CacheSetTable.Type), nameof(CacheSetTable.Value));


            #endregion

            #region LockTable
            CreateTable(nameof(LockTable))
                .WithColumn(nameof(LockTable.Key)).AsString(Defaults.DefaultLength).PrimaryKey()
                .WithColumn(nameof(LockTable.Name)).AsString().NotNullable()
                .WithColumn(nameof(LockTable.Count)).AsInt32().NotNullable()
                .WithColumn(nameof(LockTable.AcquiredUtcTime)).AsDateTime()
                .WithColumn(nameof(LockTable.ExpireUtcTime)).AsDateTime()
                // 持有者标识：抢占/续期/释放的作用域限定，防止原持有者动别人的锁
                .WithColumn(nameof(LockTable.Owner)).AsString(Defaults.UuidLength).NotNullable().WithDefaultValue(string.Empty);

            #endregion

            #region QueueTable

            CreateTable(nameof(QueueTable))
                .WithColumn(nameof(QueueTable.Id)).AsInt64().PrimaryKey().Identity()
                .WithColumn(nameof(QueueTable.WorkId)).AsString(Defaults.UuidLength).NotNullable()
                .WithColumn(nameof(QueueTable.Queue)).AsString(Defaults.DefaultLength).NotNullable()
                // 抢占标记（原迁移 1002）：出队"条件 UPDATE + 按标记回读"，机器名 + 唯一 Id
                .WithColumn(nameof(QueueTable.FetchedBy)).AsString(Defaults.DefaultLength).Nullable()
                .WithColumn(nameof(QueueTable.FetchUtcTime)).AsDateTime().Nullable();

            CreateIndx("idx_queuetable_queue",nameof(QueueTable))
                .OnColumn(nameof(QueueTable.Queue))
                .Ascending()
                .OnColumn(nameof(QueueTable.FetchUtcTime)) .Ascending();

            #endregion

            #region RecurringTable

            CreateTable(nameof(RecurringTable))
                .WithColumn(nameof(RecurringTable.Id))
                .AsString(Defaults.DefaultLength).PrimaryKey()
                .WithColumn(nameof(RecurringTable.Name))
                .AsString(Defaults.DefaultLength)
                .WithColumn(nameof(RecurringTable.Cron))
                .AsString(Defaults.UuidLength)
                .WithColumn(nameof(RecurringTable.JsonContent))
                .AsString(Defaults.MaxLength)
                .WithColumn(nameof(RecurringTable.Queue))
                .AsString(Defaults.UuidLength)
                .WithColumn(nameof(RecurringTable.TimeZone))
                .AsString(Defaults.UuidLength)
                .WithColumn(nameof(RecurringTable.CreateUtcTime))
                .AsDateTime()
                .WithColumn(nameof(RecurringTable.NextExecutUtcTime))
                .AsDateTime().Nullable()
                .WithColumn(nameof(RecurringTable.LastExecutUtcTime))
                .AsDateTime().Nullable()
                .WithColumn(nameof(RecurringTable.LastWorkId))
                .AsString(Defaults.UuidLength)
                .WithColumn(nameof(RecurringTable.RetryCount))
                .AsInt32()
                .WithColumn(nameof(RecurringTable.ErrorInfo))
                .AsString(Defaults.MaxLength)
                // 周期任务暂停标记（原迁移 1004，监控面板写操作）：存量数据默认不暂停
                .WithColumn(nameof(RecurringTable.Paused)).AsBoolean().NotNullable().WithDefaultValue(false);

            #endregion

            #region ServerTable

            CreateTable(nameof(ServerTable))
                .WithColumn(nameof(ServerTable.ServerName))
                .AsString(Defaults.DefaultLength).PrimaryKey()
                .WithColumn(nameof(ServerTable.CreateUtcTime))
                .AsDateTime()
                .WithColumn(nameof(ServerTable.LastHeartbeatUtcTime))
                .AsDateTime()
                .WithColumn(nameof(ServerTable.JsonData))
                .AsString(Defaults.MaxLength);

            #endregion

            #region StateTable

            CreateTable(nameof(StateTable))
                .WithColumn(nameof(StateTable.Id))
                .AsString(Defaults.UuidLength).PrimaryKey()
                .WithColumn(nameof(StateTable.Name))
                .AsString(Defaults.UuidLength)
                .WithColumn(nameof(StateTable.WorkId))
                .AsString(Defaults.UuidLength)
                .WithColumn(nameof(StateTable.Info))
                .AsString(Defaults.MaxLength)
                // datetime2 而非 datetime：SqlServer 的 datetime 只有 1/300 秒精度且会四舍五入，
                // 同一毫秒内写入的多条状态（如 processing→succeeded）会塌成同一值，
                // 令"最后一条状态"（GetLastState 按时间排序）与卡死恢复的"是否存在更晚状态"误判。
                // SQLite 下两种声明同是 NUMERIC 亲和度、落库均为 TEXT，行为不变。
                .WithColumn(nameof(StateTable.CreateUtcTime))
                .AsDateTime2()
                .WithColumn(nameof(StateTable.JsonData))
                .AsString(Defaults.MaxLength);

            #endregion

            #region WorkTable
            CreateTable(nameof(WorkTable))
                .WithColumn(nameof(WorkTable.Id))
                .AsString(Defaults.UuidLength).PrimaryKey()
                .WithColumn(nameof(WorkTable.StateId))
                .AsString(Defaults.UuidLength)
                .WithColumn(nameof(WorkTable.StateName))
                .AsString(Defaults.DefaultLength)
                .WithColumn(nameof(WorkTable.Name))
                .AsString(Helper.MaxWorkNameLength).NotNullable().WithDefaultValue(string.Empty)
                .WithColumn(nameof(WorkTable.InvocationJson))
                .AsString(Defaults.MaxLength)
                .WithColumn(nameof(WorkTable.ParametersJson))
                .AsString(Defaults.MaxLength)
                .WithColumn(nameof(WorkTable.CreateUtcTime)).AsDateTime()
                .WithColumn(nameof(WorkTable.ExpireUtcTime)).AsDateTime().Nullable();

            // 监控面板需要按任务名检索（原迁移 1001）
            CreateIndx("idx_worktable_name", nameof(WorkTable))
                .OnColumn(nameof(WorkTable.Name)).Ascending();

            CreateForeignKey("fk_workstate_workid", nameof(StateTable), nameof(StateTable.WorkId), nameof(WorkTable), nameof(WorkTable.Id));
            #endregion

            #region BatchTable
            CreateTable(nameof(BatchTable))
                .WithColumn(nameof(BatchTable.BatchId)).AsString(Defaults.UuidLength).PrimaryKey()
                .WithColumn(nameof(BatchTable.ParentBatchId)).AsString(Defaults.UuidLength).Nullable()
                .WithColumn(nameof(BatchTable.Name))
                .AsString(Helper.MaxWorkNameLength).NotNullable().WithDefaultValue(string.Empty)
                .WithColumn(nameof(BatchTable.Status)).AsInt32()
                .WithColumn(nameof(BatchTable.Message)).AsString(Defaults.MaxLength).Nullable()
                .WithColumn(nameof(BatchTable.RetryCount)).AsInt32()
                .WithColumn(nameof(BatchTable.Count)).AsInt32()
                .WithColumn(nameof(BatchTable.DealCount)).AsInt32()
                .WithColumn(nameof(BatchTable.ExpireUtcTime)).AsDateTime2()
                .WithColumn(nameof(BatchTable.CreateUtcTime)).AsDateTime2()
                .WithColumn(nameof(BatchTable.UpdateUtcTime)).AsDateTime2();

            CreateTable(nameof(BatchItemTable))
                .WithColumn(nameof(BatchItemTable.Id)).AsString(Defaults.UuidLength).PrimaryKey()
                .WithColumn(nameof(BatchItemTable.BatchId)).AsString(Defaults.UuidLength)
                .WithColumn(nameof(BatchItemTable.Name))
                .AsString(Helper.MaxWorkNameLength).NotNullable().WithDefaultValue(string.Empty)
                .WithColumn(nameof(BatchItemTable.WorkId)).AsString(Defaults.UuidLength).Nullable()
                .WithColumn(nameof(BatchItemTable.Status)).AsInt32()
                .WithColumn(nameof(BatchItemTable.Message)).AsString(Defaults.MaxLength).Nullable()
                .WithColumn(nameof(BatchItemTable.WorkJson)).AsString(Defaults.MaxLength)
                .WithColumn(nameof(BatchItemTable.DataJson)).AsString(Defaults.MaxLength)
                .WithColumn(nameof(BatchItemTable.DataType)).AsString(Defaults.MaxLength)
                .WithColumn(nameof(BatchItemTable.CreateUtcTime)).AsDateTime2()
                .WithColumn(nameof(BatchItemTable.UpdateUtcTime)).AsDateTime2();

            CreateForeignKey("fk_batchitem_btchid", nameof(BatchItemTable), nameof(BatchItemTable.BatchId), nameof(BatchTable), nameof(BatchTable.BatchId));
            #endregion

            #region SagaTable
            // 编排式分布式事务（原迁移 1003）：正向串行推进 + 失败逆序补偿。
            // SagaType / InputJson 支撑执行期实例还原（D2=B）；步骤行只存方法名（D1），
            // 执行由 SagaStepRunner 还原实例后按名调用。
            CreateTable(nameof(SagaTable))
                .WithColumn(nameof(SagaTable.SagaId)).AsString(Defaults.UuidLength).PrimaryKey()
                .WithColumn(nameof(SagaTable.Name)).AsString(Helper.MaxWorkNameLength).NotNullable()
                .WithColumn(nameof(SagaTable.CorrelationId)).AsString(Helper.MaxWorkNameLength).NotNullable()
                .WithColumn(nameof(SagaTable.SagaType)).AsString(Defaults.MaxLength).NotNullable()
                .WithColumn(nameof(SagaTable.InputJson)).AsString(Defaults.MaxLength).Nullable()
                .WithColumn(nameof(SagaTable.Status)).AsInt32()
                .WithColumn(nameof(SagaTable.StepCount)).AsInt32()
                .WithColumn(nameof(SagaTable.CurrentIndex)).AsInt32()
                .WithColumn(nameof(SagaTable.CompensationIndex)).AsInt32()
                .WithColumn(nameof(SagaTable.VariablesJson)).AsString(Defaults.MaxLength).Nullable()
                .WithColumn(nameof(SagaTable.Message)).AsString(Defaults.MaxLength).Nullable()
                .WithColumn(nameof(SagaTable.ExpireUtcTime)).AsDateTime2()
                .WithColumn(nameof(SagaTable.CreateUtcTime)).AsDateTime2()
                .WithColumn(nameof(SagaTable.UpdateUtcTime)).AsDateTime2();

            // 业务关联键唯一：同一业务重复启动 saga 时返回既有实例（幂等创建）
            CreateIndx("idx_sagatable_correlation", nameof(SagaTable))
                .OnColumn(nameof(SagaTable.CorrelationId)).Ascending()
                .WithOptions().Unique();

            // 超时扫描按 (Status, CreateUtcTime) 过滤
            CreateIndx("idx_sagatable_status", nameof(SagaTable))
                .OnColumn(nameof(SagaTable.Status)).Ascending()
                .OnColumn(nameof(SagaTable.CreateUtcTime)).Ascending();
            #endregion

            #region SagaStepTable
            CreateTable(nameof(SagaStepTable))
                .WithColumn(nameof(SagaStepTable.StepId)).AsString(Defaults.UuidLength).PrimaryKey()
                .WithColumn(nameof(SagaStepTable.SagaId)).AsString(Defaults.UuidLength).NotNullable()
                .WithColumn(nameof(SagaStepTable.Index)).AsInt32()
                .WithColumn(nameof(SagaStepTable.Name)).AsString(Helper.MaxWorkNameLength).NotNullable()
                .WithColumn(nameof(SagaStepTable.ForwardMethod)).AsString(Defaults.DefaultLength).NotNullable()
                .WithColumn(nameof(SagaStepTable.CompensateMethod)).AsString(Defaults.DefaultLength).Nullable()
                .WithColumn(nameof(SagaStepTable.OutputKey)).AsString(Helper.MaxWorkNameLength).Nullable()
                .WithColumn(nameof(SagaStepTable.Status)).AsInt32()
                .WithColumn(nameof(SagaStepTable.Message)).AsString(Defaults.MaxLength).Nullable()
                .WithColumn(nameof(SagaStepTable.ForwardWorkId)).AsString(Defaults.UuidLength).Nullable()
                .WithColumn(nameof(SagaStepTable.CompensateWorkId)).AsString(Defaults.UuidLength).Nullable()
                .WithColumn(nameof(SagaStepTable.CreateUtcTime)).AsDateTime2()
                .WithColumn(nameof(SagaStepTable.UpdateUtcTime)).AsDateTime2();

            // 步骤按序号取用（游标定位、串行遍历），(SagaId, Index) 唯一顺带保证序号不重复
            CreateIndx("idx_sagastep_sagaid", nameof(SagaStepTable))
                .OnColumn(nameof(SagaStepTable.SagaId)).Ascending()
                .OnColumn(nameof(SagaStepTable.Index)).Ascending()
                .WithOptions().Unique();

            CreateForeignKey("fk_sagastep_sagaid", nameof(SagaStepTable), nameof(SagaStepTable.SagaId), nameof(SagaTable), nameof(SagaTable.SagaId));
            #endregion
        }
    }
}
