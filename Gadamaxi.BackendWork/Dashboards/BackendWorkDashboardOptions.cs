namespace Gadamaxi.BackendWork.Dashboards
{
    /// <summary>
    /// 监控面板配置项（核心库持有：仓储实现与面板层共用，均只依赖核心库）。
    /// 读操作默认对"任意已认证用户"开放；宿主可通过 <see cref="AuthorizationPolicy"/> 指定自定义策略。
    /// </summary>
    public class BackendWorkDashboardOptions
    {
        /// <summary>页面与 API 的路由前缀（API 统一在 {RoutePrefix}/api 下）。默认 /backendwork-dashboard</summary>
        public string RoutePrefix { get; set; } = "/backendwork-dashboard";

        /// <summary>读操作授权策略名。null（默认）= 要求已认证用户（RequireAuthorization()）</summary>
        public string? AuthorizationPolicy { get; set; }

        /// <summary>全局硬只读开关：true（默认）时即使写端点已注册也拒绝一切写操作</summary>
        public bool ReadOnly { get; set; } = true;

        /// <summary>是否注册写操作端点（重试/取消/手动触发/暂停恢复）。默认 false</summary>
        public bool EnableWriteOperations { get; set; } = false;

        /// <summary>写操作独立授权策略名（如 "BackendWorkAdmin"）。启用写操作时必须提供</summary>
        public string? WritePolicy { get; set; }

        /// <summary>任务慢执行阈值（秒）：并发视图中运行超过该时长的任务高亮。默认 60</summary>
        public int SlowTaskThresholdSeconds { get; set; } = 60;

        /// <summary>
        /// 节点心跳失联判定阈值（秒）。默认 60。
        /// 挂载面板时会按 DI 中的 WorkServerOption.HeartbeatDelay 自动校准为不小于 2×心跳间隔，
        /// 避免心跳间隔（默认 5 分钟）大于阈值时节点被误判为失联。
        /// 注意框架自身死节点清理阈值独立配置（默认 25 分钟）
        /// </summary>
        public int NodeOfflineThresholdSeconds { get; set; } = 60;

        /// <summary>Saga 卡住判定阈值（分钟）：Running 且超过该时长无更新即视为卡住。默认 30</summary>
        public int StalledSagaThresholdMinutes { get; set; } = 30;

        /// <summary>是否脱敏任务参数与异常信息（对长文本截断）。默认 true</summary>
        public bool RedactSensitiveData { get; set; } = true;

        /// <summary>脱敏截断长度。默认 2000</summary>
        public int RedactMaxLength { get; set; } = 2000;

        /// <summary>规范化路由前缀（去尾斜杠，空值回退默认）。</summary>
        public string NormalizedPrefix => string.IsNullOrWhiteSpace(RoutePrefix)
            ? "/backendwork-dashboard"
            : RoutePrefix.TrimEnd('/');
    }
}
