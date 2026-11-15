using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// 一个 saga 步骤：声明<b>正向</b>与<b>补偿</b>两个方法名（都定义在 saga 实现类上）。
    /// <para>
    /// 不额外引入"方向"列：一个步骤行用 <see cref="Status"/> 表达它当前处于哪条路径，
    /// 两个方向各自记录自己展开出的工作 Id，便于监控回溯。
    /// 执行期由 <c>SagaStepRunner</c> 还原实例后按方法名调用。
    /// </para>
    /// </summary>
    public class SagaStep
    {
        public SagaStep()
        {
            StepId = Helper.NewId();
            SagaId = string.Empty;
            Name = string.Empty;
            ForwardMethod = string.Empty;
            CompensateMethod = string.Empty;
            OutputKey = string.Empty;
            Status = SagaStepStatus.Init;
            Message = string.Empty;
            ForwardWorkId = string.Empty;
            CompensateWorkId = string.Empty;
            CreateUtcTime = Helper.CurrentUtcTime();
            UpdateUtcTime = CreateUtcTime;
        }

        public string StepId { get; set; }

        public string SagaId { get; set; }

        /// <summary>步骤序号（1 起），游标即按该值定位步骤</summary>
        public int Index { get; set; }

        /// <summary>步骤名。默认取正向方法名（D4），也可在声明时显式指定</summary>
        public string Name { get; set; }

        /// <summary>正向方法名（saga 实现类上的 public 实例方法）</summary>
        public string ForwardMethod { get; set; }

        /// <summary>补偿方法名；<b>空串表示该步骤不可补偿</b>（创建时由 StepNoCompensate 显式标注）</summary>
        public string CompensateMethod { get; set; }

        /// <summary>正向成功时把返回值写入变量池的键；空表示不采集</summary>
        public string OutputKey { get; set; }

        public SagaStepStatus Status { get; set; }

        public string Message { get; set; }

        /// <summary>展开出的正向工作 Id（流业务可据此追溯执行链路）</summary>
        public string ForwardWorkId { get; set; }

        /// <summary>展开出的补偿工作 Id</summary>
        public string CompensateWorkId { get; set; }

        public DateTime CreateUtcTime { get; set; }

        public DateTime? UpdateUtcTime { get; set; }

        /// <summary>该步骤是否声明了补偿动作</summary>
        public bool HasCompensate()
        {
            return !string.IsNullOrEmpty(CompensateMethod);
        }

        /// <summary>补偿工作的任务名（正向名为方法名，补偿名再加 <c>~undo</c>）</summary>
        public string CompensateName()
        {
            return $"{Name}{Helper.SagaCompensateNameSuffix}";
        }
    }
}
