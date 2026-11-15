using Gadamaxi.BackendWork.Works;
using Gadamaxi.BackendWork.Works.States;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Batchs
{
    public class BatchContext
    {
        public BatchContext() 
        {
            Batch = new BatchWork();
        }

        internal BatchWork Batch { get; private set; }

        internal void AddWork(BatchItem item)
        {
            Helper.CheckArgumentNull(item,nameof(item));
            Batch.Items.Add(item);
            Batch.Count = Batch.Items.Count;

            // 子任务名统一在此派生：批量名 + 序号（序号即加入顺序，1 起）。
            // 所有 AddWork 路径（Enqueue/Schedule/Awaiting）都会经过这里，规则只需维护一份。
            if (string.IsNullOrEmpty(item.Name))
            {
                item.Name = $"{Batch.Name}{Helper.WorkNameIndexSeparator}{Batch.Count}";
            }
        }

        internal string AddWork(Workload work, IWorkState state)
        {
            Helper.CheckArgumentNull(work, nameof(work));
            Helper.CheckArgumentNull(state, nameof(state));
            var data = new BatchItemWorkData
            {
                State = new WorkStateInfo
                {
                    StateName = state.StateName,
                    Data = new Dictionary<string, string>()
                },
                Parameters = new Dictionary<string, string>()
            };

            foreach(var kv in state.GetData())
            {
                data.State.Data.Add(kv.Key, kv.Value);
            }

            var item = new BatchItem
            {
                Data = data,
                Work = work,
            };

            AddWork(item);

            return item.Id;
        }

        #region Enqueue
        public string Enqueue(Expression<Action> methodCall, string queue)
        {
            var state = new EnqueuedWorkState(string.Empty, queue);
            return AddWork(Workload.FromExpression(methodCall), state);
        }

        public string Enqueue(Expression<Action> methodCall)
        {
            return Enqueue(methodCall, string.Empty);
        }

        public string Enqueue<T>(Expression<Action<T>> methodCall, string queue)
        {
            var state = new EnqueuedWorkState(string.Empty, queue);
            return AddWork(Workload.FromExpression(methodCall), state);
        }

        public string Enqueue<T>(Expression<Action<T>> methodCall)
        {
            return Enqueue(methodCall, string.Empty);
        }

        public string Enqueue(Expression<Func<Task>> methodCall, string queue)
        {
            var state = new EnqueuedWorkState(string.Empty, queue);
            return AddWork(Workload.FromExpression(methodCall), state);
        }

        public string Enqueue(Expression<Func<Task>> methodCall)
        {
            return Enqueue(methodCall, string.Empty);
        }

        public string Enqueue<T>(Expression<Func<T, Task>> methodCall, string queue)
        {
            var state = new EnqueuedWorkState(string.Empty, queue);
            return AddWork(Workload.FromExpression(methodCall), state);
        }

        public string Enqueue<T>(Expression<Func<T, Task>> methodCall)
        {
            return Enqueue(methodCall, string.Empty);
        }

        #endregion

        #region Schedule

        public string Schedule(Workload work, TimeSpan delay)
        {
            var state = new ScheduledWorkState(delay, string.Empty);
            return AddWork(work, state);
        }

        public string Schedule(Expression<Action> methodCall, TimeSpan delay)
        {
            return Schedule(Workload.FromExpression(methodCall), delay);
        }

        public string Schedule<T>(Expression<Action<T>> methodCall, TimeSpan delay)
        {
            return Schedule(Workload.FromExpression(methodCall), delay);
        }

        public string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay)
        {
            return Schedule(Workload.FromExpression(methodCall), delay);
        }

        public string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay)
        {
            return Schedule(Workload.FromExpression(methodCall), delay);
        }
        #endregion

        #region Awaiting

        public string Awaiting(Workload work, string parentId)
        {
            var state = new AwaitingWorkState(parentId, string.Empty);
            return AddWork(work, state);
        }

        public string Awaiting(Expression<Action> methodCall, string parentId)
        {
            return Awaiting(Workload.FromExpression(methodCall), parentId);
        }

        public string Awaiting<T>(Expression<Action<T>> methodCall, string parentId)
        {
            return Awaiting(Workload.FromExpression(methodCall), parentId);
        }

        public string Awaiting(Expression<Func<Task>> methodCall, string parentId)
        {
            return Awaiting(Workload.FromExpression(methodCall), parentId);
        }

        public string Awaiting<T>(Expression<Func<T, Task>> methodCall, string parentId)
        {
            return Awaiting(Workload.FromExpression(methodCall), parentId);
        }
        #endregion
    }
}
