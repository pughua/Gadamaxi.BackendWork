using FluentMigrator;
using FluentMigrator.Builders.Create.Index;
using FluentMigrator.Builders.Create.Table;
using FluentMigrator.Infrastructure;
using System;

namespace Gadamaxi.BackendWork.Sql.DBMigrators
{
    /// <summary>
    /// 迁移基类，承载跨数据库差异的统一处理，供本目录下所有迁移共用。
    /// <para>
    /// SQLite 没有 schema（只有 attach 数据库别名），也无法通过 ALTER TABLE 追加外键，
    /// FluentMigrator 遇到这两类表达式会直接抛 NotSupportedException，
    /// 因此需要按数据库能力裁剪 DDL，才能用同一套迁移脚本同时服务 SQL Server 与 SQLite。
    /// </para>
    /// </summary>
    public abstract class SchemaMigration : Migration
    {
        private bool _isSqlite;

        /// <summary>目标数据库是否为不支持 schema 的 SQLite</summary>
        protected bool IsSqlite => _isSqlite;

        /// <summary>
        /// 实际使用的 schema 名。不支持 schema 的数据库用空字符串，
        /// FluentMigrator 的 Quoter 会据此省略标识符的 schema 前缀
        /// （见 GenericQuoter.CreateSchemaPrefixedQuotedIdentifier）。
        /// </summary>
        protected string SchemaName => _isSqlite ? string.Empty : Defaults.SchemaName;

        /// <summary>
        /// 收集 Up 表达式之前先识别目标数据库。
        /// FluentMigrator 的 Migration.Context 是 internal，只有重写 GetUpExpressions
        /// 才能拿到迁移上下文与其中的迁移处理器。
        /// </summary>
        public override void GetUpExpressions(IMigrationContext context)
        {
            _isSqlite = IsSqliteContext(context);
            base.GetUpExpressions(context);
        }

        /// <summary>目标数据库是否为 SQLite（由迁移处理器的 DatabaseType 判定）</summary>
        private static bool IsSqliteContext(IMigrationContext context)
        {
            var processor = context.ServiceProvider?.GetService(typeof(IMigrationProcessor)) as IMigrationProcessor;
            return string.Equals(processor?.DatabaseType, "SQLite", StringComparison.OrdinalIgnoreCase);
        }

        protected ICreateTableWithColumnSyntax CreateTable(string tableName)
        {
            return Create.Table(SubTableName(tableName)).InSchema(SchemaName);
        }

        protected ICreateIndexOnColumnSyntax CreateIndx(string idxName, string tableName)
        {
            return Create.Index(idxName)
                .OnTable(SubTableName(tableName)).InSchema(SchemaName);
        }

        /// <summary>创建外键约束。SQLite 不支持通过 ALTER TABLE 追加外键，直接跳过</summary>
        protected void CreateForeignKey(string fkName, string foreignTableName, string foreignColumnName, string primaryTableName, string primaryColumnName)
        {
            if (_isSqlite)
            {
                return;
            }

            Create.ForeignKey(fkName)
                .FromTable(SubTableName(foreignTableName)).InSchema(SchemaName)
                .ForeignColumn(foreignColumnName)
                .ToTable(SubTableName(primaryTableName)).InSchema(SchemaName)
                .PrimaryColumn(primaryColumnName);
        }

        /// <summary>去掉实体类名末尾的 "Table" 得到实际表名（与实体上的 [Table] 特性保持一致）</summary>
        protected string SubTableName(string tableName)
        {
            if (tableName.EndsWith(Defaults.TableNameEnd))
            {
                return tableName.Substring(0, tableName.Length - Defaults.TableNameEnd.Length);
            }
            return tableName;
        }
    }
}
