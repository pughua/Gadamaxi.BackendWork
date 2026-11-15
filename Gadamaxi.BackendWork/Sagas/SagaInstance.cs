using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// 一个 saga 实例的编排状态：阶段、两个游标（正向 / 补偿）、变量池与失败原因。
    /// <para>
    /// 两个游标是整个串行语义的核心：
    /// <list type="bullet">
    /// <item><see cref="CurrentIndex"/>：下一条待执行的<b>正向</b>步骤序号（1 起）；
    /// <c>StepCount + 1</c> 表示正向已全部完成。</item>
    /// <item><see cref="CompensationIndex"/>：下一条待补偿的步骤序号，从
    /// <c>CurrentIndex - 1</c> 递减到 0（0 表示补偿完成）。</item>
    /// </list>
    /// 因为任一时刻只展开游标指向的那一步，Saga 天然严格串行，
    /// 不需要 <c>AwaitingWorkState</c> 串联，也不依赖存储的行序。
    /// </para>
    /// </summary>
    public class SagaInstance
    {
        public SagaInstance()
        {
            SagaId = Helper.NewId();
            Name = string.Empty;
            CorrelationId = string.Empty;
            SagaType = string.Empty;
            InputJson = string.Empty;
            Status = SagaStatus.None;
            Message = string.Empty;
            CurrentIndex = Helper.One;
            CompensationIndex = Helper.Zero;
            CreateUtcTime = Helper.CurrentUtcTime();
            UpdateUtcTime = CreateUtcTime;
            ExpireUtcTime = CreateUtcTime.Add(Helper.DefaultSagaExpireTime);
        }

        public string SagaId { get; set; }

        /// <summary>人工可识别的 saga 名（监控面板使用）。步骤名由它派生（形如 "订单履约#1"）</summary>
        public string Name { get; set; }

        /// <summary>
        /// 业务关联键（订单号 / 工单号……）。唯一索引 + 幂等创建：同一业务键重复启动返回既有 saga。
        /// 因此必填，空值会让所有 saga 挤到同一个唯一键上。
        /// </summary>
        public string CorrelationId { get; set; }

        /// <summary>
        /// saga 实现类的程序集限定名（<c>AssemblyQualifiedName</c>）。
        /// 执行期由 <c>SagaStepRunner</c> 据此还原实例（D2=B 实例还原），必填。
        /// </summary>
        public string SagaType { get; set; }

        /// <summary>
        /// 启动时的输入快照（实现类实例的 JSON 序列化）。
        /// 执行期与变量池一起还原出带输入的 saga 实例。
        /// </summary>
        public string InputJson { get; set; }

        public SagaStatus Status { get; set; }

        /// <summary>步骤总数</summary>
        public int StepCount { get; set; }

        /// <summary>正向游标：下一条待执行的正向步骤序号（1 起；<c>StepCount + 1</c> 表示正向完成）</summary>
        public int CurrentIndex { get; set; }

        /// <summary>补偿游标：下一条待补偿的步骤序号（0 表示补偿完成）</summary>
        public int CompensationIndex { get; set; }

        /// <summary>
        /// 变量池：所有步骤与补偿工作共享（key → 序列化后的值文本）。
        /// 创建时由 <c>SagaContext.SetVariable</c> 填充，运行时由正向步骤的
        /// <c>OutputKey</c> 采集返回值写入，步骤负载里的 <c>${key}</c> 占位符据此求值。
        /// </summary>
        public Dictionary<string, string> Variables { get; set; } = new Dictionary<string, string>();

        /// <summary>失败 / 中止原因（正向失败原因、超时、取消原因、补偿失败原因）</summary>
        public string Message { get; set; }

        /// <summary>整体清理时刻（创建 + <see cref="Helper.DefaultSagaExpireTime"/>），仅对终态 saga 生效</summary>
        public DateTime ExpireUtcTime { get; set; }

        public DateTime CreateUtcTime { get; set; }

        public DateTime? UpdateUtcTime { get; set; }

        /// <summary>是否已到终态（正向成功 / 补偿完成 / 补偿失败待人工介入）</summary>
        public bool Finish()
        {
            return Status == SagaStatus.Completed
                || Status == SagaStatus.Compensated
                || Status == SagaStatus.Aborted;
        }
    }
}
