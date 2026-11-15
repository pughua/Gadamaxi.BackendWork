using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// saga 流程定义的收集器（框架内部构建器，由 <see cref="SagaDefinition{TSaga}"/> 驱动）：
    /// 收集步骤、变量池与 <see cref="SagaInstance"/> 骨架，最终交给 <c>SagaManager.Start</c> 落库。
    /// <para>
    /// 步骤的加入顺序即执行顺序（1 起编号），补偿按相反顺序执行。
    /// 步骤名默认取正向方法名（D4）；变量池由 <c>SetVariable</c> 显式写入（D5），
    /// 运行时再由 <c>outputKey</c> 采集正向返回值扩充。
    /// </para>
    /// </summary>
    internal class SagaContext
    {
        public SagaContext()
        {
            Saga = new SagaInstance();
        }

        internal SagaInstance Saga { get; }

        internal List<SagaStep> Steps { get; } = new List<SagaStep>();

        /// <summary>写入变量池（所有步骤与补偿工作共享）。值会被序列化后落库</summary>
        internal void SetVariable(string key, object? value)
        {
            Helper.CheckArgumentNull(key, nameof(key));
            Saga.Variables[key] = Helper.Serialize(value) ?? string.Empty;
        }

        internal string AddStep(SagaStep step)
        {
            Helper.CheckArgumentNull(step, nameof(step));

            Steps.Add(step);
            // 序号即加入顺序（1 起），游标按序号定位步骤
            step.Index = Steps.Count;
            step.SagaId = Saga.SagaId;

            // 步骤名默认由 SagaDefinition 取正向方法名；走到这里仍为空只可能是
            // 直接构造 SagaStep 的内部路径，按旧规则用 "{saga名}#{序号}" 兜底
            if (string.IsNullOrEmpty(step.Name))
            {
                step.Name = $"{Saga.Name}{Helper.WorkNameIndexSeparator}{step.Index}";
            }

            return step.StepId;
        }
    }
}
