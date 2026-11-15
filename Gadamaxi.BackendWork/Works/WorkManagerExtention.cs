using Gadamaxi.BackendWork.Works.States;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works
{
    /// <summary>
    /// IWorkManager 的表达式树扩展方法。
    /// 所有方法都要求传入 name（人工可识别的任务名，监控面板使用）。
    /// 说明：name 为必传参数，因此原先"带队列 / 不带队列"的一对重载合并为同一个方法
    /// （C# 不允许仅参数名不同的重载），队列用可选参数 queue 表达。
    /// </summary>
    public static class WorkManagerExtention
    {

        public static Task<string> AddWork(this IWorkManager manager, 
            Workload work,IWorkState state, string name)
        {
            var ctx = new CreateWorkContext(work, state);
            return manager.Create(ctx, name);
        }

        #region Enqueue
        public static Task<string> Enqueue(this IWorkManager manager,
            Expression<Action> methodCall, string name, string queue = "")
        {
            var state = new EnqueuedWorkState(string.Empty, queue);
            return manager.AddWork(Workload.FromExpression(methodCall), state, name);
        }

        public static Task<string> Enqueue<T>(this IWorkManager manager,
            Expression<Action<T>> methodCall, string name, string queue = "")
        {
            var state = new EnqueuedWorkState(string.Empty, queue);
            return manager.AddWork(Workload.FromExpression(methodCall), state, name);
        }

        public static Task<string> Enqueue(this IWorkManager manager,
            Expression<Func<Task>> methodCall, string name, string queue = "")
        {
            var state = new EnqueuedWorkState(string.Empty, queue);
            return manager.AddWork(Workload.FromExpression(methodCall), state, name);
        }

        public static Task<string> Enqueue<T>(this IWorkManager manager,
            Expression<Func<T,Task>> methodCall, string name, string queue = "")
        {
            var state = new EnqueuedWorkState(string.Empty, queue);
            return manager.AddWork(Workload.FromExpression(methodCall), state, name);
        }
        #endregion

        #region Schedule

        public static Task<string> Schedule(this IWorkManager manager,
            Workload work, TimeSpan delay, string name)
        {
            var state = new ScheduledWorkState(delay, string.Empty);
            return manager.AddWork(work, state, name);
        }

        public static Task<string> Schedule(this IWorkManager manager,
            Expression<Action> methodCall, TimeSpan delay, string name)
        {
            return manager.Schedule(Workload.FromExpression(methodCall), delay, name);
        }

        public static Task<string> Schedule<T>(this IWorkManager manager,
            Expression<Action<T>> methodCall, TimeSpan delay, string name)
        {
            return manager.Schedule(Workload.FromExpression(methodCall), delay, name);
        }

        public static Task<string> Schedule(this IWorkManager manager,
            Expression<Func<Task>> methodCall, TimeSpan delay, string name)
        {
            return manager.Schedule(Workload.FromExpression(methodCall), delay, name);
        }

        public static Task<string> Schedule<T>(this IWorkManager manager,
            Expression<Func<T, Task>> methodCall, TimeSpan delay, string name)
        {
            return manager.Schedule(Workload.FromExpression(methodCall), delay, name);
        }
        #endregion

        #region Awaiting

        public static Task<string> Awaiting(this IWorkManager manager,
            Workload work, string parentId, string name)
        {
            var state = new AwaitingWorkState(parentId, string.Empty);
            return manager.AddWork(work, state, name);
        }

        public static Task<string> Awaiting(this IWorkManager manager,
            Expression<Action> methodCall, string parentId, string name)
        {
            return manager.Awaiting(Workload.FromExpression(methodCall), parentId, name);
        }

        public static Task<string> Awaiting<T>(this IWorkManager manager,
            Expression<Action<T>> methodCall, string parentId, string name)
        {
            return manager.Awaiting(Workload.FromExpression(methodCall), parentId, name);
        }

        public static Task<string> Awaiting(this IWorkManager manager,
            Expression<Func<Task>> methodCall, string parentId, string name)
        {
            return manager.Awaiting(Workload.FromExpression(methodCall), parentId, name);
        }

        public static Task<string> Awaiting<T>(this IWorkManager manager,
            Expression<Func<T, Task>> methodCall, string parentId, string name)
        {
            return manager.Awaiting(Workload.FromExpression(methodCall), parentId, name);
        }
        #endregion
    }
}
