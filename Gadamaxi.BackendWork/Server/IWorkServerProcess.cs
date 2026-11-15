using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Server
{
    /// <summary>
    /// 服务器根进程：注册本节点、并按配置派发各工作进程。
    /// <para>
    /// 抽象出来的目的（#15）：修复前 <c>WorkServer</c> 在构造函数里直接 <c>new WorkServerProcess(...)</c>，
    /// 既不可替换也无法单测。现在以接口注入，宿主可替换实现，测试可注入替身。
    /// </para>
    /// </summary>
    public interface IWorkServerProcess : IOnceWorkProcess
    {

    }
}
