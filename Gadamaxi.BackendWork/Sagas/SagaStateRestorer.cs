using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// 执行期还原 saga 实例（D2=B 实例还原）：
    /// <list type="bullet">
    /// <item>输入快照：<c>Saga.InputJson</c>（Start 时序列化的业务输入）填充到实例属性；</item>
    /// <item>变量池：<c>Saga.Variables</c> 按<b>同名属性</b>回填（大小写不敏感，变量优先，
    /// 值按属性声明类型反序列化）—— 正向步骤的 <c>outputKey</c> 产出由此流回后续步骤。</item>
    /// </list>
    /// 注意还原是<b>每次执行重现一次</b>：步骤方法里改 <c>this</c> 不会持久化，
    /// 跨步骤传递只能靠 <c>outputKey</c> / 变量池。
    /// </summary>
    internal static class SagaStateRestorer
    {
        public static void Restore(object instance, SagaInstance saga)
        {
            Helper.CheckArgumentNull(instance, nameof(instance));
            Helper.CheckArgumentNull(saga, nameof(saga));

            if (!string.IsNullOrEmpty(saga.InputJson))
            {
                JsonConvert.PopulateObject(saga.InputJson, instance);
            }

            foreach (var kv in saga.Variables)
            {
                var property = FindProperty(instance.GetType(), kv.Key);
                if (property == null || !property.CanWrite)
                {
                    continue;
                }

                var value = Helper.Deserialize(kv.Value, property.PropertyType);
                if (value != null)
                {
                    property.SetValue(instance, value);
                }
            }
        }

        /// <summary>按名找 public 实例属性（大小写不敏感；变量池 / 参数绑定的统一口径）</summary>
        public static PropertyInfo? FindProperty(Type type, string name)
        {
            Helper.CheckArgumentNull(type, nameof(type));
            Helper.CheckArgumentNull(name, nameof(name));

            return type.GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        }
    }
}
