using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    /// <summary>一次状态变更在工作层面的"完成结论"（与具体容器无关）。</summary>
    internal enum WorkCompletion
    {
        /// <summary>尚未到达终态（仍在重试链路中），调用方不应回写任何终态</summary>
        None = 0,

        /// <summary>已成功完成</summary>
        Succeeded = 1,

        /// <summary>终态失败（重试已耗尽）</summary>
        Failed = 2
    }

    /// <summary>
    /// 状态回写的通用解释：把「一次工作状态变更」翻译成与容器无关的完成结论
    /// （<see cref="Resolve"/>），并给出伴随该结论的失败原因（<see cref="ResolveFailureMessage"/>）。
    /// <para>
    /// 批量项回写（<c>BatchChangeWorkStatePipeline</c>）与 saga 步骤回写（<c>SagaChangeWorkStatePipeline</c>）
    /// 共用这里，避免两处对"是否真的结束了、算成功还是失败、失败原因是什么"判断不一致。
    /// </para>
    /// 判定规则（必须严格遵循，见 <see cref="WorkFactory.RetryExhausted"/>）：
    /// <list type="bullet">
    /// <item><c>NewState.Finish</c> → 终态，失败状态即 <see cref="FailedWorkState"/>，否则成功；</item>
    /// <item><see cref="FailedWorkState"/> 的 <c>Finish</c> 恒为 false，因此终态失败只能靠
    /// "失败重试次数是否已用尽"判定；未用尽说明马上会被改写成延时重试，此时不产生终态。</item>
    /// </list>
    /// </summary>
    internal static class WorkCompletionResolver
    {
        public static async Task<WorkCompletion> Resolve(IWorkQuery workQuery, ChangeWorkStateContext context)
        {
            Helper.CheckArgumentNull(workQuery, nameof(workQuery));
            Helper.CheckArgumentNull(context, nameof(context));

            if (context.NewState.Finish)
            {
                return context.NewState.StateName == FailedWorkState.Name
                    ? WorkCompletion.Failed
                    : WorkCompletion.Succeeded;
            }

            if (context.NewState.StateName == FailedWorkState.Name
                && WorkFactory.RetryExhausted(await workQuery.CountState(context.WorkId, FailedWorkState.Name)))
            {
                return WorkCompletion.Failed;
            }

            return WorkCompletion.None;
        }

        /// <summary>
        /// 提取「终态失败」的原因文本，供批量项 / saga 步骤落库（成为使用者在监控里看到的失败原因）。
        /// <para>
        /// 优先取 <see cref="FailedWorkState.Exception"/> 这个<b>强类型</b>属性，而不是状态数据字典
        /// （<c>GetData(key)</c>）。原因是 <see cref="FailedWorkState"/> 的状态数据是<b>惰性填充</b>的：
        /// 只有无参 <c>GetData()</c> 被调用时才把 <c>ExceptionMessage</c> 等写进字典，而唯一的写字典时机
        /// 是部署在 <c>SetWorkState</c> 里的 <c>GetData()</c>，该写入走<b>延迟事务</b>、要到 <c>Commit()</c> 才执行。
        /// 状态回写管线恰好运行在 <c>Commit()</c> 之前，此刻读字典永远读到空；
        /// 而执行链路构造失败状态时 <c>Info</c> 传的也是空串（见 <c>QueueWorkProcess</c>）。
        /// 两个来源都拿不到，失败原因就会整个丢失，使用者只看到一个状态码。
        /// </para>
        /// 回退分支覆盖"状态对象是从库里重构出来的"情形（<c>WorkFactory.ConvertState</c> 用拷贝构造，
        /// <c>Exception</c> 为 null，但数据字典里带着落库时的 <c>ExceptionMessage</c>）。
        /// </summary>
        public static string ResolveFailureMessage(ChangeWorkStateContext context)
        {
            Helper.CheckArgumentNull(context, nameof(context));

            if (context.NewState is FailedWorkState failed && failed.Exception != null)
            {
                return failed.Exception.Message;
            }

            var detail = context.NewState.GetData($"{nameof(FailedWorkState.Exception)}Message");
            return string.IsNullOrEmpty(detail) ? context.NewState.Info ?? string.Empty : detail;
        }
    }
}
