using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork
{
    internal static class CachedExpressionCompilerHelper
    {
        private static readonly ParameterExpression UnusedParameterExpr = Expression.Parameter(typeof(object), "_unused");

        /// <summary>
        /// Evaluates an expression (not a LambdaExpression), e.g. 2 + 2.
        /// </summary>
        /// <param name="arg"></param>
        /// <returns>Expression result.</returns>
        public static object Evaluate(Expression arg)
        {
            if (arg == null)
            {
                throw new ArgumentNullException(nameof(arg));
            }

            Func<object, object> func = Wrap(arg);
            return func(null);
        }

        private static Func<object, object> Wrap(Expression arg)
        {
            var lambdaExpr = Expression.Lambda<Func<object, object>>(Expression.Convert(arg, typeof(object)), UnusedParameterExpr);
            return Gadamaxi.BackendWork.ExpressionUtil.CachedExpressionCompiler.Process(lambdaExpr);
        }
    }
}
