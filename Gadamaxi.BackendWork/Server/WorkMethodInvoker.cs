using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    /// <summary>
    /// 方法调用的统一收口：同步方法直接返回结果；awaitable（Task / Task&lt;T&gt; / ValueTask / ValueTask&lt;T&gt;）
    /// 等待后取结果。判定依据是<b>声明的返回类型</b>，与既有语义保持一致。
    /// <para>
    /// <c>WorkActivator</c>（普通工作）与 <see cref="Sagas.SagaStepRunner"/>（saga 步骤）两处共用，
    /// 避免"怎么调一个方法、怎么取返回值"出现两份实现。
    /// </para>
    /// </summary>
    internal static class WorkMethodInvoker
    {
        public static async Task<object?> Invoke(MethodInfo method, object? instance, object?[] args)
        {
            Helper.CheckArgumentNull(method, nameof(method));

            // awaitable 判定必须看方法的<b>返回类型</b>：承载类永远不是 Task，
            // 且同步方法返回值里即使装着 Task 也按既有语义原样返回、不等待
            if (Helper.IsAwaitableType(method.ReturnType))
            {
                var rs = method.Invoke(instance, args);

                // 有没有返回值只能由<b>声明的返回类型</b>决定，不能看运行时类型：
                // Task.CompletedTask 的运行时类型是 Task<VoidTaskResult>，
                // async 方法的运行时类型是 AsyncStateMachineBox<>（Task<T> 的派生类）。
                var hasResult = method.ReturnType.IsGenericType;

                Task task;
                if (rs is Task rawTask)
                {
                    // Task<T> 派生自 Task，非泛型的 Task.CompletedTask / async Task 也都落在这里
                    task = rawTask;
                }
                else if (rs != null
                    && rs.GetType().IsGenericType
                    && rs.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    // 装箱的 ValueTask<T> 与 ValueTask 没有继承关系，只能经 AsTask 等待；
                    // 必须排在非泛型 ValueTask 之前，否则会漏掉 await 直接返回
                    task = (Task)rs.GetType().GetMethod(nameof(ValueTask<int>.AsTask))!.Invoke(rs, null)!;
                }
                else if (rs is ValueTask plainValueTask)
                {
                    task = plainValueTask.AsTask();
                }
                else
                {
                    return null;
                }

                await task;

                return hasResult
                    ? task.GetType().GetProperty(nameof(Task<int>.Result))?.GetValue(task)
                    : null;
            }

            return method.Invoke(instance, args);
        }
    }
}
