using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Gadamaxi.BackendWork.Sagas;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// saga 用例的公共基础设施：把「推进」与「执行」两件事拆开手动驱动。
    /// <para>
    /// 不用订阅式 <see cref="QueueWorkProcess.Execute"/>（长轮询会阻塞），
    /// 而是按 saga 步骤记录的工作 Id 逐条 <see cref="QueueWorkProcess.Perform"/>，
    /// 这样用例对时序完全可控，失败时也容易定位链路断在哪一步。
    /// </para>
    /// 同一驱动逻辑被推进层与端到端层共用，故收在这里。
    /// </summary>
    public abstract class SagaTestBase : BackendWorkTestBase
    {
        protected ISagaManager Saga { get; }
        internal SagaWorkProcess SagaProcess { get; }
        internal SagaTimeoutProcess TimeoutProcess { get; }
        internal QueueWorkProcess QueueProcess { get; }
        protected WorkProcessContext Context { get; }

        protected SagaTestBase() : base()
        {
            TestJobs.Reset();
            SagaTestJobs.Reset();

            // 缩短推进轮询让用例尽快结束
            Option.SagaPollingDelay = TimeSpan.FromMilliseconds(50);
            // 只重试 1 次：让"最终失败"在两次执行内就到达，避免用例为了等重试耗尽而变慢
            Option.WorkRetryCount = 1;
            Option.WorkRetryDelay = TimeSpan.Zero;

            Saga = Provider.GetRequiredService<ISagaManager>();
            SagaProcess = new SagaWorkProcess(Lock, Query, Queue,
                Provider.GetRequiredService<IWorkStateHandlerProvider>(),
                CreateLogger<SagaWorkProcess>(), Option);
            TimeoutProcess = new SagaTimeoutProcess(Query, Lock, CreateLogger<SagaTimeoutProcess>(), Option);
            QueueProcess = new QueueWorkProcess(CreateLogger<QueueWorkProcess>());
            Context = NewProcessContext();
        }

        /// <summary>
        /// 逐轮驱动到 saga 终态：先推进一轮游标（可能展开新的步骤工作），
        /// 再把该 saga 已展开的全部步骤工作逐条执行一次。
        /// 工作已被执行完时 <c>Perform</c> 会直接确认返回，不会重复执行任务方法。
        /// </summary>
        protected async Task<bool> DriveUntilFinish(string sagaId, TimeSpan? timeout = null)
        {
            var limit = timeout ?? TimeSpan.FromSeconds(30);
            var watch = Stopwatch.StartNew();

            while (watch.Elapsed < limit)
            {
                await SagaProcess.Execute(Context);

                foreach (var workId in await StepWorkIds(sagaId))
                {
                    await QueueProcess.Perform(Context, new QueueInfo
                    {
                        WorkId = workId,
                        QueueName = Helper.DefaultQueue
                    });
                }

                var saga = await Query.GetSagaById(sagaId);
                if (saga != null && saga.Finish())
                {
                    return true;
                }

                await Task.Delay(20);
            }

            return false;
        }

        /// <summary>saga 已展开的全部步骤工作 Id（正向与补偿）</summary>
        protected async Task<List<string>> StepWorkIds(string sagaId)
        {
            var steps = await Query.GetSagaSteps(sagaId);
            var ids = new List<string>();
            foreach (var step in steps)
            {
                if (!string.IsNullOrEmpty(step.ForwardWorkId))
                {
                    ids.Add(step.ForwardWorkId);
                }

                if (!string.IsNullOrEmpty(step.CompensateWorkId))
                {
                    ids.Add(step.CompensateWorkId);
                }
            }

            return ids;
        }

        protected Task<SagaInstance?> GetSaga(string sagaId) => Query.GetSagaById(sagaId);

        protected Task<List<SagaStep>> GetSteps(string sagaId) => Query.GetSagaSteps(sagaId);

        /// <summary>把 saga 的开始时间改到过去，模拟"整体超时"（超时判定在库侧比较时间）</summary>
        protected Task AgeSaga(string sagaId, TimeSpan age) => Db.AgeSagaAsync(sagaId, age);

        /// <summary>把 saga 的清理时刻改到过去，用于验证过期级联清理</summary>
        protected Task ExpireSaga(string sagaId) => Db.ExpireSagaAsync(sagaId);
    }
}
