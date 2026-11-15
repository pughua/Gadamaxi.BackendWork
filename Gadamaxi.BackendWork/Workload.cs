using Gadamaxi.BackendWork.Resources;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork
{
    public class Workload
    {
        public Workload(
            Type type,
            MethodInfo info,
            IReadOnlyList<object> args,
            string queue = "")
        {
            Type = type;
            MethodInfo = info;
            Arguments = args;
            Queue = queue;
        }
        public Type Type { get; private set; }
        public MethodInfo MethodInfo { get; private set; }
        public IReadOnlyList<object> Arguments { get; private set; }

        public string Queue { get; set; }

        public string Serialize(bool excludeArguments = false)
        {
            var content = SerializeContent(excludeArguments);
            return Helper.Serialize(content)!;
        }

        public WorkloadContent SerializeContent(bool excludeArguments = false)
        {
            var typeName = Type.FullName;
            var methodName = MethodInfo.Name;
            var assemblyName = Type.Assembly.FullName;

            var ptypes = MethodInfo.GetParameters().Select(p => p.ParameterType.FullName).ToArray();
            var parameterTypes = Helper.Serialize(ptypes);

            var arguments = Helper.SerializeToList(Arguments);
            var argumentStr = Helper.Serialize(arguments);

            if (excludeArguments)
            {
                return new WorkloadContent(typeName, methodName, parameterTypes!, string.Empty, Queue);
            }
            else
            {
                return new WorkloadContent(typeName, methodName, parameterTypes!, argumentStr!, Queue);
            }

        }

        #region static method
        public static Workload Deserialize(string content)
        {
            var workContent = Helper.Deserialize<WorkloadContent>(content);

            var load = Deserialize(workContent);
            return load;
        }

        public static Workload Deserialize(WorkloadContent content)
        {
            var typeInfo = Helper.GetType(content.Type);

            var arguments = new List<object>();
            var argList = Helper.Deserialize<List<string>>(content.Arguments);
            var paramList = Helper.Deserialize<List<string>>(content.ParameterTypes);
            var paramTypes = new Type[argList.Count];
            for (var i = 0; i < argList.Count; i++)
            {
                // 与主类型解析保持同一口径（#17）：用 Helper.GetType（带程序集解析器），
                // 而不是裸 Type.GetType —— 后者跨程序集/泛型/可空引用类型（如 List<string>）会解析失败返回 null。
                var type = Helper.GetType(paramList[i]);
                paramTypes[i] = type;
                if (string.IsNullOrEmpty(argList[i]))
                {
                    arguments.Add(null);
                }
                else
                {
                    var argobj = Helper.Deserialize(argList[i], type);
                    arguments.Add(argobj);
                }
            }

            // 方法匹配只按参数类型（#17）：旧实现把"泛型参数类型的个数"当成 GetMethod 的
            // 泛型方法元数传入，导致带 List<string>/int? 这类参数的方法永远匹配不到。
            var methodInfo = typeInfo.GetMethod(content.Method, paramTypes);
            if (methodInfo == null)
            {
                throw new KeyNotFoundException(string.Format(Resource.MethodNotFound, content.Type, content.Method));
            }

            var queue = content.Queue == null ? string.Empty : content.Queue;

            return new Workload(typeInfo!, methodInfo!, arguments!.AsReadOnly(), queue);
        }

        public static Workload FromExpression(Expression<Func<Task>> methodCall, string queue)
        {
            return FromExpression(methodCall, null, queue);
        }

        public static Workload FromExpression(Expression<Func<Task>> methodCall)
        {
            return FromExpression(methodCall, string.Empty);
        }

        public static Workload FromExpression<T>(Expression<Func<T,Task>> methodCall, string queue)
        {
            return FromExpression(methodCall, typeof(T), queue);
        }

        public static Workload FromExpression<T>(Expression<Func<T,Task>> methodCall)
        {
            return FromExpression(methodCall, string.Empty);
        }

        public static Workload FromExpression(Expression<Action> methodCall, string queue)
        {
            return FromExpression(methodCall, null, queue);
        }

        public static Workload FromExpression(Expression<Action> methodCall)
        {
            return FromExpression(methodCall,string.Empty);
        }

        public static Workload FromExpression<T>(Expression<Action<T>> methodCall, string queue)
        {
            return FromExpression(methodCall, typeof(T), queue);
        }

        public static Workload FromExpression<T>(Expression<Action<T>> methodCall)
        {
            return FromExpression(methodCall, string.Empty);
        }

        internal static Workload FromExpression(LambdaExpression methodCall, Type explicitType, string queue)
        {
            if (methodCall == null) throw new ArgumentNullException(nameof(methodCall));

            var callExpression = methodCall.Body as MethodCallExpression;
            if (callExpression == null)
            {
                throw new ArgumentException("Expression body should be of type `MethodCallExpression`", nameof(methodCall));
            }

            var type = explicitType ?? callExpression.Method.DeclaringType;
            var method = callExpression.Method;

            if (explicitType == null && callExpression.Object != null)
            {
                var objectValue = GetExpressionValue(callExpression.Object);
                if (objectValue == null)
                {
                    throw new InvalidOperationException("Expression object should be not null.");
                }

                type = objectValue.GetType();

                method = type.GetNonOpenMatchingMethod(
                    callExpression.Method.Name,
                    callExpression.Method.GetParameters().Select(x => x.ParameterType).ToArray());
            }

            var objs = GetExpressionValues(callExpression.Arguments);

            return new Workload(type, method, objs);
        }

        private static object[] GetExpressionValues(ReadOnlyCollection<Expression> expressions)
        {
            var result = expressions.Count > 0 ? new object[expressions.Count] : new object[] { };
            var index = 0;

            foreach (var expression in expressions)
            {
                result[index++] = GetExpressionValue(expression);
            }

            return result;
        }

        private static object GetExpressionValue(Expression expression)
        {
            if (expression is ConstantExpression constantExpression)
            {
                return constantExpression.Value;
            }
            else if (expression is MemberExpression member)
            {
                if (member.Expression is ConstantExpression constantExpression1)
                {
                    var type = constantExpression1.Value.GetType();
                    var field = type.GetField(member.Member.Name);
                    var value = field.GetValue(constantExpression1.Value);
                    return value;
                }
            }
            return CachedExpressionCompilerHelper.Evaluate(expression);

        }
        #endregion
    }
}
