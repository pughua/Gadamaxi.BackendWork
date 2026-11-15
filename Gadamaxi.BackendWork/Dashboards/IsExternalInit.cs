// netstandard2.1 缺少 IsExternalInit 类型，record 的 init 访问器编译需要它；
// 仅为核心库 Dashboards 模型提供编译垫片，运行时无行为。
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
