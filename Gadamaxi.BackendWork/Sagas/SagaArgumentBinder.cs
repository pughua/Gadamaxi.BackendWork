using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// 步骤方法的参数按名绑定（V2'' 的执行期实现）：
    /// 变量池优先（运行时产物，如前序步骤的 <c>outputKey</c>），其次还原后的实例属性（业务输入）。
    /// <para>
    /// 绑定源在 <c>Start</c> 时已由 <see cref="SagaStartValidator"/> 校验过；
    /// 这里找不到时只能落 null（比如步骤在补偿路径上跑、而它依赖的产出从未发生），由业务方法自行兜底。
    /// </para>
    /// </summary>
    internal static class SagaArgumentBinder
    {
        public static object?[] Bind(MethodInfo method, object instance,
            IDictionary<string, string> variables)
        {
            Helper.CheckArgumentNull(method, nameof(method));
            Helper.CheckArgumentNull(instance, nameof(instance));
            Helper.CheckArgumentNull(variables, nameof(variables));

            var parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                return Array.Empty<object?>();
            }

            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var name = parameter.Name!;

                // 变量池优先（大小写不敏感）：运行时产物比启动快照新鲜
                var text = FindVariable(variables, name);
                if (!string.IsNullOrEmpty(text))
                {
                    args[i] = Helper.Deserialize(text, parameter.ParameterType);
                    continue;
                }

                var property = SagaStateRestorer.FindProperty(instance.GetType(), name);
                args[i] = property is { CanRead: true } ? property.GetValue(instance) : null;
            }

            return args;
        }

        private static string? FindVariable(IDictionary<string, string> variables, string name)
        {
            foreach (var kv in variables)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value;
                }
            }

            return null;
        }
    }
}
