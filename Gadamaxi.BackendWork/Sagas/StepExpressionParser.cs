using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;
using Gadamaxi.BackendWork.Exceptions;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// 解析 <c>SagaDefinition</c> 里的步骤声明 lambda（<c>s => s.方法()</c>），并做启动期校验 V1/V2'/V3。
    /// <para>
    /// 只提取方法名 —— 步骤工作在执行期由 <see cref="SagaStepRunner"/> 还原实例后按名调用，
    /// 声明期的表达式不会被序列化进工作负载。
    /// </para>
    /// </summary>
    internal static class StepExpressionParser
    {
        /// <summary>解析出方法名与方法元数据；不满足声明规则直接抛错（不落库）。</summary>
        public static ParsedStep Parse(LambdaExpression lambda)
        {
            Helper.CheckArgumentNull(lambda, nameof(lambda));

            // 委托返回类型与 lambda 体类型不同（如 Task<string> → Task）时，编译器会包一层 Convert
            var body = lambda.Body;
            while (body is UnaryExpression unary
                && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.Quote))
            {
                body = unary.Operand;
            }

            if (!(body is MethodCallExpression call))
            {
                throw new BackendException("步骤声明必须是形如 `s => s.方法()` 的方法调用。");
            }

            // V1：调用目标必须是 lambda 参数本身 —— 保证方法就声明在 saga 实现类上
            if (!(call.Object is ParameterExpression))
            {
                throw new BackendException(
                    $"步骤 {call.Method.Name} 必须声明在 saga 实现类上（`s => s.{call.Method.Name}()`）。");
            }

            // V3：public 实例方法（静态方法无法表达"还原后的实例"语义，private 方法不构成对外契约）
            if (call.Method.IsStatic || !call.Method.IsPublic)
            {
                throw new BackendException(
                    $"步骤方法 {call.Method.Name} 必须是 saga 实现类的 public 实例方法。");
            }

            // V2'：参数一律按名绑定，声明处不许传"实参"（避免"两处绑定源"的歧义与误导）。
            // 但表达式树不允许省略可选参数（CS0854）、也不能零参调用带参方法（CS7036），
            // 因此带参步骤的声明只能写 `s => s.方法(s.属性名)`：实参位置必须是 lambda 参数的成员访问，
            // 它只是绑定源的显式注记（编译期可见），真正绑定仍严格按"参数名"在执行期解析。
            foreach (var arg in call.Arguments)
            {
                if (arg is not MemberExpression member || member.Expression is not ParameterExpression)
                {
                    throw new BackendException(
                        $"步骤方法 {call.Method.Name} 的参数按名从输入/变量池绑定：声明处不传参，" +
                        $"或只写绑定源注记 `s => s.{call.Method.Name}(s.属性名)`。");
                }
            }

            return new ParsedStep(call.Method.Name);
        }

        /// <summary>解析结果：目前只需要方法名。</summary>
        public sealed class ParsedStep
        {
            public ParsedStep(string methodName)
            {
                MethodName = methodName;
            }

            public string MethodName { get; }
        }
    }
}
