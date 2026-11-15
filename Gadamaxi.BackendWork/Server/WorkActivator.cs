using Gadamaxi.BackendWork.Storages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    /// <summary>
    /// 工作实例的激活与调用：实例创建走 <see cref="InstanceFactory"/>（D3：DI 优先、回退 Activator），
    /// 调用走 <see cref="WorkMethodInvoker"/>（同步 / awaitable 统一收口）。
    /// </summary>
    internal class WorkActivator : IDisposable
    {
        private readonly object? instance;
        private readonly Workload _work;
        private readonly bool _owned;

        public WorkActivator(Workload work)
        {
            _work = work;

            if (_work.MethodInfo.IsStatic)
            {
                // 静态方法不需要实例
                instance = null;
                _owned = false;
            }
            else if (_work.Type.IsInterface)
            {
                // 接口类型只能来自容器（既有语义）
                instance = DependencyInjectionHelper.Get(_work.Type, false);
                if (instance == null)
                {
                    Helper.CannotCreateInstance(_work.Type);
                }

                _owned = false;
            }
            else
            {
                instance = InstanceFactory.Create(_work.Type, out _owned);
            }
        }

        public Task<object?> Invoke()
        {
            return WorkMethodInvoker.Invoke(_work.MethodInfo, instance, _work.Arguments.ToArray());
        }

        public void Dispose()
        {
            // 只释放自己 new 的：容器给的实例（尤其 Singleton）不能被这里释放
            if (_owned && instance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
