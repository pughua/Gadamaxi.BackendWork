using System;
using System.Collections.Generic;

namespace Gadamaxi.BackendWork.Dashboards
{
    /// <summary>
    /// 监控面板 DTO 与响应模型（核心库持有，面板层与仓储实现共用）。
    /// 时间一律规范化为 UTC（序列化带 Z），由前端按本地时区展示。
    /// </summary>
    public static class DashboardModels
    {
        public static DateTime? Utc(DateTime? value) =>
            value == null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

        /// <summary>
        /// 从出队抢占标记还原节点名。
        /// 出队时写入的 FetchedBy 形如 "{节点名}#{本次抢占唯一标记}"（唯一后缀用于并发抢占回读判定），
        /// 面板/统计只需要"{节点名}"部分，与 Server.ServerName 对齐。
        /// 历史数据（旧版写的是机器名#标记）无 # 或前缀不匹配时按原样返回。
        /// </summary>
        public static string? NodeName(string? fetchedBy)
        {
            if (string.IsNullOrEmpty(fetchedBy)) return fetchedBy;
            var idx = fetchedBy!.IndexOf('#');
            return idx > 0 ? fetchedBy[..idx] : fetchedBy;
        }

        public static string? Redact(string? text, BackendWorkDashboardOptions options)
        {
            if (string.IsNullOrEmpty(text) || !options.RedactSensitiveData) return text;
            return text.Length <= options.RedactMaxLength ? text : text[..options.RedactMaxLength] + "…(已截断)";
        }
    }

    public record PagedResult<T>(IReadOnlyList<T> Items, int Total);

    public record TaskListItem(
        string Id, string Name, string State, string? Queue, string? Node,
        DateTime CreateUtcTime, int RetryCount);

    public record StateHistoryItem(string Name, string Info, DateTime CreateUtcTime);

    public record TaskDetail(
        string Id, string Name, string State, string? Queue, string? Node,
        DateTime CreateUtcTime, DateTime? ExpireUtcTime,
        string InvocationJson, string ParametersJson,
        IReadOnlyList<StateHistoryItem> StateHistory);

    public record QueueStat(string Queue, int Depth, int Fetched);

    public record NodeConcurrency(string Node, int Processing, int WorkerCount);

    public record ProcessingItem(string WorkId, string Name, string? Node, DateTime StartTime, double RunSeconds);

    public record ConcurrencyView(
        int ProcessingTotal, int SlowTotal,
        IReadOnlyList<QueueStat> Queues,
        IReadOnlyList<NodeConcurrency> Nodes,
        IReadOnlyList<ProcessingItem> Processing);

    public record BatchListItem(
        string BatchId, string Name, string? ParentBatchId, string Status,
        int Count, int DealCount, int RetryCount, string Message,
        DateTime CreateUtcTime, DateTime? UpdateUtcTime, DateTime ExpireUtcTime);

    public record BatchItemRow(
        string Id, string Name, string BatchId, string Status, string? WorkId,
        string Message, DateTime CreateUtcTime, DateTime? UpdateUtcTime);

    public record BatchDetail(BatchListItem Batch, IReadOnlyList<BatchItemRow> Items);

    public record RecurringItem(
        string Id, string Name, string Cron, string Queue, string TimeZone,
        bool Paused, DateTime? NextExecuteUtcTime, DateTime? LastExecuteUtcTime,
        string LastWorkId, int RetryCount, string ErrorInfo);

    public record SagaListItem(
        string SagaId, string Name, string CorrelationId, string Status,
        int StepCount, int CurrentIndex, int CompensationIndex,
        DateTime CreateUtcTime, DateTime? UpdateUtcTime, bool Stalled);

    public record SagaStepItem(
        string StepId, int Index, string Name, string Status,
        string Message, string ForwardWorkId, string CompensateWorkId,
        string OutputKey, DateTime CreateUtcTime, DateTime? UpdateUtcTime);

    public record SagaDetail(
        SagaListItem Saga,
        string VariablesJson, string Message, DateTime ExpireUtcTime,
        IReadOnlyList<SagaStepItem> Steps);

    public record NodeItem(
        string ServerName, bool Online, DateTime StartedUtcTime, DateTime LastHeartbeatUtcTime,
        IReadOnlyList<string> Queues, int Processing);

    public record OverviewView(
        int OnlineNodes, int RegisteredNodes,
        IReadOnlyList<KeyValuePair<string, int>> StateCounts,
        int ProcessingTotal, int EnqueuedTotal,
        int SucceededToday, int FailedToday, double? FailureRateToday,
        IReadOnlyList<QueueStat> Queues,
        int RecurringTotal, int RecurringPaused,
        int SagaRunning, int SagaCompensating, int SagaStalled,
        int TotalWorkers,
        int BatchTotal, int BatchRunning, int BatchFailure);
}
