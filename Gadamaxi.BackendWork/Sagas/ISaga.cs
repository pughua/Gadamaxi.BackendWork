using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// saga 的业务契约：实现类既声明流程（步骤与补偿都写在自己身上），也承载业务输入。
    /// <para>
    /// 通常通过继承 <see cref="Saga{TSaga}"/>（CRTP 基类）来实现，从而获得默认 saga 名
    /// 与 <c>Configure</c> 的编译期类型安全；直接实现本接口也可以，但流程定义将无法声明。
    /// </para>
    /// </summary>
    public interface ISaga
    {
        /// <summary>业务关联键（幂等键），由实现类按业务语义给出。必填、唯一。</summary>
        string CorrelationId { get; }

        /// <summary>saga 名（监控面板）。基类默认取类名，可 override。</summary>
        string Name { get; }
    }
}
