using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// saga 实现类的 CRTP 基类：<c>class UpdateAddressSaga : Saga&lt;UpdateAddressSaga&gt;</c>。
    /// <para>
    /// 泛型参数让 <c>SagaDefinition&lt;TSaga&gt;</c> 的 lambda 参数 <c>s</c> 的<b>静态类型就是实现类本身</b>，
    /// 因此"步骤与补偿只能声明为本类方法"由编译器保证。
    /// </para>
    /// <para>
    /// 实现类有两个互不重叠的角色：
    /// <list type="bullet">
    /// <item><b>启动时</b>：输入载体 + 流程定义器 —— 调用方 <c>new</c> 出来填好业务输入再交给
    /// <c>ISagaManager.Start</c>，框架序列化输入快照（<c>Saga.InputJson</c>）并调用 <c>Configure</c> 展开步骤。</item>
    /// <item><b>执行时</b>：无状态步骤宿主 —— 每次执行由框架还原出一个带输入、带变量的实例
    /// （还原源 = <c>Saga.InputJson</c> + 变量池按名回填同名属性），再调用步骤方法。
    /// 因此步骤方法里改 <c>this</c> 不会持久化，跨步骤传递只能靠 <c>outputKey</c> / 变量池。</item>
    /// </list>
    /// </para>
    /// </summary>
    public abstract class Saga<TSaga> : ISaga where TSaga : Saga<TSaga>
    {
        /// <summary>saga 名（监控面板）。默认取实现类类名，可 override。</summary>
        public virtual string Name => typeof(TSaga).Name;

        /// <summary>业务关联键（幂等键）。由实现类按业务语义给出。</summary>
        public abstract string CorrelationId { get; }

        /// <summary>声明流程。步骤与补偿<b>只能声明为本类的方法</b>。</summary>
        protected abstract void Configure(SagaDefinition<TSaga> definition);

        /// <summary>框架内部入口：让 SagaManager 在拿到静态类型 TSaga 的位置调用 Configure。</summary>
        internal void Build(SagaDefinition<TSaga> definition)
        {
            Helper.CheckArgumentNull(definition, nameof(definition));
            Configure(definition);
        }
    }
}
