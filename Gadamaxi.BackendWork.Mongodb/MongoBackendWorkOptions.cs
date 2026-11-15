using MongoDB.Driver;
using System;

namespace Gadamaxi.BackendWork.Mongodb
{
    /// <summary>
    /// Mongodb 存储层配置。
    /// <para>
    /// <b>事务前提</b>：<see cref="Storages.IWorkStorage"/> 的提交语义要求"一次 Commit 内的所有写入要么全部可见、要么全部不可见"，
    /// 这在 Mongodb 上依赖<b>多文档事务</b>，而多文档事务仅在<b>副本集 / 分片集群</b>上可用。
    /// 单机（standalone）部署不支持事务，此时存储层会退化为逐条写入并记警告日志——
    /// 原子性不再有保证，因此<b>生产环境请务必使用副本集</b>（哪怕只有一个成员）。
    /// </para>
    /// </summary>
    public class MongoBackendWorkOptions
    {
        public MongoBackendWorkOptions(string connectionString, string databaseName)
        {
            Helper.CheckArgumentNull(connectionString, nameof(connectionString));
            Helper.CheckArgumentNull(databaseName, nameof(databaseName));

            ConnectionString = connectionString;
            DatabaseName = databaseName;
        }

        /// <summary>连接串，形如 <c>mongodb://127.0.0.1:27017</c>。</summary>
        public string ConnectionString { get; set; }

        public string DatabaseName { get; set; }

        /// <summary>
        /// 集合名前缀（可选）。同一库内多套后台任务共存时用它隔离。
        /// </summary>
        public string CollectionPrefix { get; set; } = string.Empty;

        /// <summary>
        /// 为 true 时，单机部署（不支持多文档事务）不再静默退化，而是在首次提交时直接抛异常。
        /// 建议生产开启，避免"以为有事务、实际没有"。
        /// </summary>
        public bool RequireTransactions { get; set; }

        /// <summary>额外的客户端设置（连接池、超时等）。</summary>
        public Action<MongoClientSettings>? ConfigureClient { get; set; }

        public MongoClientSettings BuildSettings()
        {
            var settings = MongoClientSettings.FromConnectionString(ConnectionString);

            ConfigureClient?.Invoke(settings);

            return settings;
        }
    }
}
