using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// saga 步骤工作的执行入口：工作负载指向它，由它还原 saga 实例再调用真正的步骤方法。
    /// <para>
    /// 之所以不由 <c>WorkActivator</c> 直接调业务方法：还原需要 <c>sagaId</c>（在工作参数里、
    /// 不在负载参数里），把这份"saga 专属知识"放进通用激活器会污染执行链路，因此收口在 <see cref="Sagas"/> 内。
    /// 步骤工作的负载是 <c>SagaStepRunner.Run(sagaId, stepId, isCompensate)</c>，工作名仍是步骤方法名（D4）。
    /// </para>
    /// </summary>
    internal class SagaStepRunner
    {
        public SagaStepRunner()
        {
            // 显式 public 无参构造：执行期由 InstanceFactory 回退 Activator 创建
            // （internal 类的默认构造是 internal，Activator.CreateInstance 只认 public）
        }

        /// <param name="sagaId">saga Id</param>
        /// <param name="stepId">步骤 Id</param>
        /// <param name="isCompensate">true=执行补偿方法，false=执行正向方法</param>
        public async Task<object?> Run(string sagaId, string stepId, bool isCompensate)
        {
            Helper.CheckArgumentNull(sagaId, nameof(sagaId));
            Helper.CheckArgumentNull(stepId, nameof(stepId));

            var query = DependencyInjectionHelper.Get<IWorkQuery>();

            var saga = await query.GetSagaById(sagaId)
                       ?? throw new BackendException($"Saga {sagaId} not exists; the step work is stale.");

            var step = (await query.GetSagaSteps(sagaId)).FirstOrDefault(t => t.StepId == stepId)
                       ?? throw new BackendException($"Saga step {stepId} not exists.");

            var methodName = isCompensate ? step.CompensateMethod : step.ForwardMethod;
            if (string.IsNullOrEmpty(methodName))
            {
                throw new BackendException(
                    $"Saga {sagaId} step {step.Index} has no {(isCompensate ? "compensate" : "forward")} method.");
            }

            var type = Helper.GetType(saga.SagaType)
                       ?? throw new BackendException($"Saga type '{saga.SagaType}' not found.");

            // 1) 建实例（DI 优先、回退 Activator —— 与 WorkActivator 共用同一工厂）
            var instance = InstanceFactory.Create(type, out var owned);
            // 只有自己 new 的才由这里释放；容器给的实例（尤其 Singleton）交还容器
            var disposable = owned ? instance as IDisposable : null;
            try
            {
                // 2) 还原：输入快照 + 变量池（变量优先，只回填存在的同名可写属性）
                SagaStateRestorer.Restore(instance, saga);

                // 3) 定位方法 + 按名绑参（无参则直接调用）
                var method = FindMethod(type, methodName)
                             ?? throw new BackendException(
                                 $"Method '{methodName}' not found on {type.Name}.");
                var args = SagaArgumentBinder.Bind(method, instance, saga.Variables);

                // 4) 调用。异常原样冒泡给执行链路（落库前会解包 TargetInvocationException），
                //    返回值交给状态机（outputKey 由回写管线采集）
                return await WorkMethodInvoker.Invoke(method, instance, args);
            }
            finally
            {
                disposable?.Dispose();
            }
        }

        private static System.Reflection.MethodInfo? FindMethod(Type type, string name)
        {
            return type.GetMethods().FirstOrDefault(m =>
                m.Name == name && m.IsPublic && !m.IsStatic);
        }
    }
}
