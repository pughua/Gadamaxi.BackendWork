using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Gadamaxi.BackendWork.Server
{
    public class WorkServerOption
    {
        public string ServerName { get; set; } = string.Empty;

        public bool UseBackendServer { get; set; } = true;

        /// <summary>兼容旧拼写（UserBackendServer）：请改用 <see cref="UseBackendServer"/>。</summary>
        [Obsolete("拼写修正：请改用 UseBackendServer。")]
        public bool UserBackendServer
        {
            get => UseBackendServer;
            set => UseBackendServer = value;
        }

        public List<string> Queues { get; set; } = new List<string>() { Helper.DefaultQueue };

        /// <summary>
        /// 工作线程数（消费并发度）。为 0 时自动取 <c>Min(ProcessorCount*2, 15)</c>（#15）。
        /// </summary>
        public int WorkerCount { get; set; } = Helper.Zero;

        /// <summary>
        /// 每轮批量扫描上限（#22）：出队、缓存扫描、批量项扫描每轮最多处理多少条。
        /// 积压时可调大以提升消费速度；为 0 时回退到 <see cref="Helper.DefaultBatchSize"/>。
        /// </summary>
        public int BatchSize { get; set; } = Helper.DefaultBatchSize;

        /// <summary>
        /// 同一批消息之间的处理间隔（#16）：用于削峰，默认 100ms。为 0 时回退到 <see cref="Helper.DefaultWorkIntervalDelay"/>。
        /// </summary>
        public TimeSpan WorkIntervalDelay { get; set; } = Helper.DefaultWorkIntervalDelay;

        /// <summary>
        /// 生效的批量扫描上限：未配置（&lt;=0）时回退到 <see cref="Helper.DefaultBatchSize"/>。
        /// 出队、缓存扫描、批量项扫描等多处共用同一口径（#22）。
        /// </summary>
        public int GetBatchSize() => BatchSize > Helper.Zero ? BatchSize : Helper.DefaultBatchSize;

        public TimeSpan BatchExpireTime { get; set; } = Helper.DefaultBatchExpireUtcTime;

        public int BatchRetryCount { get; set; } = Helper.DefaultBatchRetryCount;

        public int WorkRetryCount { get; set; } = Helper.DefaultWorkRetryCount;

        public TimeSpan WorkRetryDelay { get; set; } = TimeSpan.FromMinutes(1);


        public TimeSpan RecurringPollingDelay { get; set; } = Helper.DefaultRecurringPollingDelay;
        public TimeSpan SchedulePollingDelay { get; set; }=  Helper.DefaultSchedulerPollingDelay;
        public TimeSpan WorkPollingDelay { get; set; }= Helper.DefaultWorkPollingDelay;
        public TimeSpan HeartbeatDelay { get; set; } = Helper.DefaultHeartbeatDelay;
        public TimeSpan RecoveryPollingDelay { get; set; } = Helper.DefaultRecoveryPollingDelay;

        /// <summary>
        /// saga 推进轮询间隔（默认 1 秒）。Saga 是严格串行的「一步一轮」推进，
        /// 该值直接决定步骤之间的空转延迟，因此比批量/周期的轮询间隔小得多。
        /// </summary>
        public TimeSpan SagaPollingDelay { get; set; } = Helper.DefaultSagaPollingDelay;

        /// <summary>
        /// saga 整体执行超时（默认 30 分钟）：从创建起算，超时仍处于 Running 则转入逆序补偿。
        /// 置为 <see cref="TimeSpan.MaxValue"/> 等价于关闭超时判定（不推荐）。
        /// </summary>
        public TimeSpan SagaTimeout { get; set; } = Helper.DefaultSagaTimeout;

        /// <summary>
        /// saga 超时扫描进程的轮询间隔（默认 1 分钟）。超时判定是分钟级语义，
        /// 不必跟随 1 秒的推进轮询，否则只是白白增加查询量。
        /// </summary>
        public TimeSpan SagaTimeoutPollingDelay { get; set; } = Helper.DefaultSagaTimeoutPollingDelay;

        /// <summary>
        /// 终态 saga 的保留时长（默认 7 天）：与 <see cref="BatchExpireTime"/> 同一口径，
        /// 过期后由清理进程连同步骤一起删除，避免表无限膨胀。
        /// </summary>
        public TimeSpan SagaExpireTime { get; set; } = Helper.DefaultSagaExpireTime;

        /// <summary>
        /// 过期数据清理进程轮询间隔：定期删除已过期的终态工作/批量及其从表行，避免表无限膨胀。
        /// </summary>
        public TimeSpan CleanupPollingDelay { get; set; } = Helper.DefaultCleanupPollingDelay;

        /// <summary>
        /// 心跳死判阈值：节点超过该时长未更新心跳即判定为死亡并清理。
        /// 参照 Hangfire（约 5×心跳间隔），默认 25 分钟（= 5 × 默认心跳间隔 5 分钟），可调。
        /// </summary>
        public TimeSpan ServerDeadThreshold { get; set; } = TimeSpan.FromMinutes(25);

        /// <summary>
        /// 卡死任务判定阈值：最后状态为 processing 且超过该时长、
        /// 且无执行者持有工作锁时，将被 RecoveryWorkProcess 重新入队。
        /// </summary>
        public TimeSpan WorkProcessingTimeout { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// 优雅停机超时（#9）：停止取新任务后，等待在途任务完成的最长时间。
        /// 超过该时长仍在执行的工作不再等待（工作线程是后台线程，进程可正常退出），
        /// 停机流程记 warning 日志后返回。设为 <see cref="TimeSpan.Zero"/> 表示不等待（等同立即停机）。
        /// </summary>
        public TimeSpan ShutdownTimeout { get; set; } = Helper.DefaultShutdownTimeout;
    }
}
