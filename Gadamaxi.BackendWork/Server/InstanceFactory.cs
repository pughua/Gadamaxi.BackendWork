using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Server
{
    /// <summary>
    /// 工作 / saga 实例的创建：DI 优先、回退 <see cref="Activator"/>（D3=B）。
    /// <para>
    /// <c>WorkActivator</c> 与 <see cref="Sagas.SagaStepRunner"/> 两处共用（同一逻辑只留一份）。
    /// 回退路径不依赖容器，所以业务类不注册也能跑（前提是有 public 无参构造）。
    /// </para>
    /// </summary>
    internal static class InstanceFactory
    {
        /// <param name="type">实例类型（不能是接口）</param>
        /// <param name="owned">true=框架自己 new 的，由调用方负责 Dispose；false=容器给的，交还容器</param>
        public static object Create(Type type, out bool owned)
        {
            Helper.CheckArgumentNull(type, nameof(type));

            object? obj = null;
            try
            {
                // 未注册返回 null；Scoped 从根解析会抛（宿主开 ValidateScopes 时），捕获后回退
                obj = DependencyInjectionHelper.Get(type, false);
            }
            catch (InvalidOperationException)
            {
                obj = null;
            }

            if (obj != null)
            {
                owned = false;
                return obj;
            }

            var created = Activator.CreateInstance(type);
            if (created == null)
            {
                Helper.CannotCreateInstance(type);
            }

            owned = true;
            return created!;
        }
    }
}
