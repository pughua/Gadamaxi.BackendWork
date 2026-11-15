using System;
using System.Collections.Generic;
using System.Reflection;
using Gadamaxi.BackendWork.Exceptions;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// saga 启动期校验（D6）：所有能在 <c>Start</c> 时发现的问题都挡在落库之前，
    /// 不让"半夜执行时才崩"的错误溜进调度。
    /// <list type="bullet">
    /// <item>V4 —— <c>TSaga</c> 已注册 DI（推荐 Transient）或有 public 无参构造，否则执行期建不出实例；</item>
    /// <item>V2'' —— 步骤方法的每个参数名都能在输入属性 / 已声明变量 / 前序步骤 outputKey 中解析，否则执行期静默拿到 null；</item>
    /// <item>V5 —— 步骤名不重复且满足任务名规则（补偿名还要再加 "~undo" 后缀）；</item>
    /// <item>V6 —— 输入快照可 JSON 序列化，否则落库才失败；</item>
    /// <item>V7 —— <c>outputKey</c> 合法且不重复，否则变量池互相覆盖。</item>
    /// </list>
    /// （V1/V2'/V3 在 <see cref="StepExpressionParser"/> 解析声明时已检查。）
    /// </summary>
    internal static class SagaStartValidator
    {
        public static void Validate<TSaga>(TSaga saga, SagaContext context) where TSaga : Saga<TSaga>
        {
            Helper.CheckArgumentNull(saga, nameof(saga));
            Helper.CheckArgumentNull(context, nameof(context));

            var type = typeof(TSaga);

            // V4：实例可创建（DI 已注册，或存在 public 无参构造）
            if (!CanCreateInstance(type))
            {
                throw new BackendException(
                    $"Saga '{saga.Name}' ({type.Name}) has no public parameterless constructor and is not registered in DI; " +
                    "register it as Transient or add a public parameterless constructor.");
            }

            // V6：输入可序列化（执行期还原实例的唯一来源）
            try
            {
                Helper.Serialize(saga);
            }
            catch (Exception ex)
            {
                throw new BackendException(
                    $"Saga '{saga.Name}' input is not serializable: {ex.Message}", ex);
            }

            // V5/V7：步骤名与 outputKey 的唯一性 + 任务名规则
            var stepNames = new HashSet<string>(StringComparer.Ordinal);
            var outputKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in context.Steps)
            {
                if (!stepNames.Add(step.Name))
                {
                    throw new BackendException($"Saga '{saga.Name}' has duplicate step name '{step.Name}'.");
                }

                Helper.CheckWorkName(step.Name);
                if (step.HasCompensate())
                {
                    Helper.CheckWorkName(step.CompensateName());
                }

                if (!string.IsNullOrEmpty(step.OutputKey) && !outputKeys.Add(step.OutputKey))
                {
                    throw new BackendException(
                        $"Saga '{saga.Name}' has duplicate outputKey '{step.OutputKey}'; variables would overwrite each other.");
                }
            }

            // V2''：参数绑定源（正向只能用此前步骤的产出；补偿在正向之后执行，可用全部 outputKey）
            var variables = context.Saga.Variables.Keys;
            var outputsSoFar = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in context.Steps)
            {
                CheckBinding(type, saga.Name, step.Name, step.ForwardMethod, variables, outputsSoFar);

                if (!string.IsNullOrEmpty(step.OutputKey))
                {
                    outputsSoFar.Add(step.OutputKey);
                }
            }

            foreach (var step in context.Steps)
            {
                if (step.HasCompensate())
                {
                    CheckBinding(type, saga.Name, step.CompensateName(), step.CompensateMethod,
                        variables, outputKeys);
                }
            }
        }

        private static bool CanCreateInstance(Type type)
        {
            try
            {
                if (DependencyInjectionHelper.Get(type, false) != null)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // 容器拒绝解析（如 Scoped 从根解析）：视同未注册，走无参构造判定
            }

            return type.GetConstructor(Type.EmptyTypes) != null;
        }

        private static void CheckBinding(Type type, string sagaName, string displayName,
            string methodName, ICollection<string> variables, ISet<string> allowedOutputs)
        {
            var method = FindPublicMethod(type, methodName);
            if (method == null)
            {
                throw new BackendException(
                    $"Saga '{sagaName}' step '{displayName}' method '{methodName}' not found on {type.Name}.");
            }

            foreach (var parameter in method.GetParameters())
            {
                var name = parameter.Name!;
                if (HasProperty(type, name) || variables.Contains(name) || allowedOutputs.Contains(name))
                {
                    continue;
                }

                throw new BackendException(
                    $"Saga '{sagaName}' step '{displayName}' parameter '{name}' can not be bound; " +
                    "rename it to an input property, a declared variable or an earlier outputKey.");
            }
        }

        private static MethodInfo? FindPublicMethod(Type type, string name)
        {
            foreach (var method in type.GetMethods())
            {
                if (method.Name == name && method.IsPublic && !method.IsStatic)
                {
                    return method;
                }
            }

            return null;
        }

        /// <summary>公开为 internal 供 <see cref="SagaArgumentBinder"/> 复用同一套"按名找属性"口径</summary>
        internal static bool HasProperty(Type type, string name)
        {
            return SagaStateRestorer.FindProperty(type, name) != null;
        }
    }
}
