using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// <see cref="Saga{TSaga}.Configure"/> 的声明式定义入口。
    /// <para>
    /// lambda 只用来<b>点出方法名</b>（形如 <c>s => s.UpdateAddress()</c>），不传实参 ——
    /// 步骤方法的参数在执行期按名从输入快照 / 变量池 / 前序步骤产出绑定（见
    /// <see cref="SagaStartValidator"/> 的 V2'' 与 <see cref="SagaArgumentBinder"/>）。
    /// 带参方法的声明受表达式树限制（不能零参调用、不能省略可选参数），
    /// 只允许写绑定源注记 <c>s => s.Consume(s.OrderNo)</c> —— 实参位置必须是
    /// <c>s.属性名</c> 的成员访问，传常量等真实参会被拒绝（V2'）。
    /// </para>
    /// </summary>
    public sealed class SagaDefinition<TSaga> where TSaga : Saga<TSaga>
    {
        private readonly SagaContext _context;

        internal SagaDefinition(SagaContext context)
        {
            _context = context;
        }

        /// <summary>声明一个「正向 + 补偿」步骤（异步方法）</summary>
        /// <param name="outputKey">正向成功时把返回值写入变量池的键；空表示不采集</param>
        /// <param name="name">步骤名；空则取正向方法名（D4）</param>
        public void Step(Expression<Func<TSaga, Task>> forward,
            Expression<Func<TSaga, Task>> compensate,
            string outputKey = "", string name = "")
        {
            AddStep(forward, compensate, outputKey, name);
        }

        /// <summary>声明一个「正向 + 补偿」步骤（同步方法）</summary>
        public void Step(Expression<Action<TSaga>> forward,
            Expression<Action<TSaga>> compensate,
            string outputKey = "", string name = "")
        {
            AddStep(forward, compensate, outputKey, name);
        }

        /// <summary>
        /// 声明一个<b>不可补偿</b>步骤（只读查询 / 通知类）。
        /// 回滚时不会被展开补偿工作，而是记为 <see cref="SagaStepStatus.Skipped"/> 并在步骤消息里留痕。
        /// </summary>
        public void StepNoCompensate(Expression<Func<TSaga, Task>> forward,
            string outputKey = "", string name = "")
        {
            AddStep(forward, null, outputKey, name);
        }

        /// <summary>声明一个<b>不可补偿</b>步骤（同步方法）</summary>
        public void StepNoCompensate(Expression<Action<TSaga>> forward,
            string outputKey = "", string name = "")
        {
            AddStep(forward, null, outputKey, name);
        }

        /// <summary>
        /// 写入变量池（D5：显式）。变量会回填到同名属性，也会按名绑给步骤参数。
        /// </summary>
        public void SetVariable(string key, object? value)
        {
            _context.SetVariable(key, value);
        }

        private void AddStep(LambdaExpression forward, LambdaExpression? compensate,
            string outputKey, string name)
        {
            Helper.CheckArgumentNull(forward, nameof(forward));

            var f = StepExpressionParser.Parse(forward);
            var c = compensate == null ? null : StepExpressionParser.Parse(compensate);

            _context.AddStep(new SagaStep
            {
                // D4：步骤名默认取正向方法名；同名冲突由启动校验（V5）拦截
                Name = string.IsNullOrEmpty(name) ? f.MethodName : name,
                ForwardMethod = f.MethodName,
                // 空串 = 该步骤不可补偿（由 StepNoCompensate 显式标注）
                CompensateMethod = c == null ? string.Empty : c.MethodName,
                OutputKey = outputKey ?? string.Empty
            });
        }
    }
}
