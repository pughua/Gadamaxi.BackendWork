using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Resources;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Gadamaxi.BackendWork.Server
{
    public static class WorkFactory
    {
        /// <summary>
        /// 将已存在的工作重新入队（延时调度、卡死恢复等场景共用的通用实现）。
        /// 持有工作锁后校验工作与状态，满足条件才转 Enqueued（由状态处理器重新发布队列消息）。
        /// 返回是否实际执行了重新入队。
        /// </summary>
        /// <param name="stateCheck">可选的状态校验：返回 true 才允许重新入队</param>
        public static async Task<bool> RequeueWork(IWorkQuery workQuery,
            IDistributedLock distributedLock,
            string workId,
            string trigger,
            Func<IWorkState?, bool>? stateCheck = null)
        {
            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                using (await distributedLock.AcquireLock(workId, Helper.DistributedLockTimeOut))
                {
                    var data = await workQuery.GetWorkData(workId);
                    if (data == null)
                    {
                        return false;
                    }

                    var lastState = await workQuery.GetLastState(workId);
                    if (lastState != null && lastState.Finish)
                    {
                        return false;
                    }

                    if (stateCheck != null && !stateCheck(lastState))
                    {
                        return false;
                    }

                    var queue = data.Content.Queue;
                    if (string.IsNullOrEmpty(queue))
                    {
                        var option = DependencyInjectionHelper.Get<WorkServerOption>();
                        queue = option.Queues.First();
                    }

                    var state = new EnqueuedWorkState(string.Format(Resource.TriggerBy, trigger), queue);
                    await ChangeState(storage, workId, state, data.StateName, data);

                    await storage.RemoveCacheSet(EnqueuedWorkState.Name, workId);
                    await storage.Commit();
                }
            }

            return true;
        }

        public static async Task TriggerRecurringWork(IWorkStorage storage,
            IWorkQuery workQuery, IWorkQueue workQueue,
            IWorkStateHandlerProvider provider,
            string id,
            string trigger,
            ILogger? logger=null
            )
        {
            var recurring = await workQuery.GetRecurringWorkById(id);
            if (recurring == null)
            {
                await storage!.RemoveRecurringWork(id);
                await storage.RemoveCacheSet(Helper.RecurringKey, id);
            }
            else
            {
                var now = Helper.CurrentUtcTime();
                // CAS 版本号：触发推进前的 LastExecuteUtcTime。并发触发者必须基于同一快照才能推进，
                // 否则后到者会因版本不匹配而失败，从而避免同一周期被重复创建多个工作（#11）。
                var expectedLastExecut = recurring.LastExecuteUtcTime;
                recurring.GenNextExecuteTime(recurring.LastExecuteUtcTime ?? recurring.CreateUtcTime, now);

                // Paused（监控面板暂停标记）：暂停期间只推进缓存戳、不触发，恢复后自然续跑
                if (!recurring.Paused && recurring.CheckCurrentTimeRun(now.UnixMillisecondStamp()))
                {
                    // 先以 CAS 抢占"本次触发"，且必须是触发事务的第一个写入：
                    // 若已被其它触发者（手动触发 / 另一调度节点 / 本进程重叠的 Timer）抢先，
                    // AdvanceRecurringWork 会在提交时抛 RecurringDuplicateTriggerException
                    // 并回滚整个事务，本次不会写入任何工作（#11 防重）。
                    recurring.LastExecuteUtcTime = now;
                    await storage.AdvanceRecurringWork(recurring, expectedLastExecut);

                    var workLoad = Workload.Deserialize(recurring.WorkContent);

                    var parameters = new Dictionary<string, object>
                    {
                        {"RecurringId",recurring.Id}
                    };
                    var workId = await CreateEnqueueWork(storage, workQueue, provider, workLoad, now, trigger, parameters, recurring.Name);

                    // 抢占成功：把刚创建的工作 Id 写回（同一事务提交）。
                    recurring.LastWorkId = workId;
                    await storage.UpdateRecurringWork(recurring);
                }

                var nextStamp = recurring.NextExecuteUtcTime!.Value.UnixMillisecondStamp()-Helper.One;
                await storage.UpdateCacheSet(Helper.RecurringKey,
                        nextStamp,
                        id);
            }
        }

        /// <summary>
        /// 以 CAS 守卫收口一次延迟事务写入：先排队写入，再提交；若提交因 CAS 抢占失败而抛异常，
        /// 则整个事务已回滚（不会留下半个写入），记为 Info 日志并返回 false。
        /// <para>
        /// 存储写入是延迟事务，<c>Commit()</c> 之前拿不到受影响行数，因此 CAS 抢占失败只能以
        /// "0 行 → 抛异常"来表达（#11 周期任务触发确立的做法）。周期任务触发与 saga 推进两处
        /// 共用本收口，避免各写一遍 try/catch 并让"CAS 冲突是正常并发、不是故障"这一约定走样。
        /// </para>
        /// </summary>
        /// <param name="conflictMessage">CAS 冲突时的日志前缀（区分是哪个调用方被抢先）</param>
        /// <returns>是否提交成功。false 表示本次抢占已被别的推进者处理</returns>
        public static async Task<bool> CommitWithCasGuard(IWorkStorage storage, ILogger? logger,
            Func<Task> write, string conflictMessage)
        {
            Helper.CheckArgumentNull(storage, nameof(storage));
            Helper.CheckArgumentNull(write, nameof(write));

            await write();

            try
            {
                await storage.Commit();
                return true;
            }
            catch (RecurringDuplicateTriggerException ex)
            {
                logger?.LogInformation($"{conflictMessage} {ex.Message}");
                return false;
            }
            catch (SagaConcurrentUpdateException ex)
            {
                logger?.LogInformation($"{conflictMessage} {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 判断连续任务的父任务是否已经结束（父任务不存在时同样视为结束）。
        /// 父任务已结束时子任务必须立即入队，否则它会永久等待一个不会再来的触发。
        /// 创建管线（StateCreatePipeline）与 awaiting 状态处理器共用该判定。
        /// </summary>
        public static async Task<bool> ParentWorkFinished(IWorkQuery workQuery, string parentId)
        {
            if (string.IsNullOrEmpty(parentId))
            {
                return true;
            }

            if (!await workQuery.CheckWorkExist(parentId))
            {
                return true;
            }

            var lastState = await workQuery.GetLastState(parentId);
            return lastState != null && lastState.Finish;
        }

        /// <summary>
        /// 失败重试次数是否已用尽。用尽之后失败状态不会再被改写为延时重试，工作停在 failed。
        /// 判定"工作是否已到终态"必须走这里：<see cref="FailedWorkState.Finish"/> 恒为 false，
        /// 只看 Finish 会把终态失败误判成"仍在执行"。
        /// 失败重试（DefaultChangeStatePipeline）与批量项终态回写两处共用该规则。
        /// </summary>
        public static bool RetryExhausted(int failedCount)
        {
            var option = DependencyInjectionHelper.Get<WorkServerOption>();
            return failedCount >= option.WorkRetryCount;
        }

        /// <summary>
        /// 读取等待指定父任务的连续子任务 Id（awaiting 缓存集合）。
        /// 父任务唤醒子任务与子任务自查两条路径共用。
        /// </summary>
        public static Task<List<string>> GetAwaitingChildren(IWorkQuery workQuery, string parentWorkId)
        {
            return workQuery.GetCacheSet($"{AwaitingWorkState.Name}:{parentWorkId}",
                Helper.Zero, long.MaxValue, int.MaxValue);
        }

        /// <summary>
        /// 唤醒等待父任务的连续子任务（父任务结束后的通用触发实现）。
        /// 只处理仍处于 awaiting 的子任务，避免重复入队造成重复执行。
        /// </summary>
        /// <param name="storage">
        /// 传入时入队写入该存储（与调用方同一事务）；为空时为每次入队使用独立存储并立即提交。
        /// </param>
        public static async Task EnqueueAwaitingChildren(IWorkQuery workQuery, string parentWorkId,
            IWorkStorage? storage = null)
        {
            var ids = await GetAwaitingChildren(workQuery, parentWorkId);
            foreach (var id in ids)
            {
                var sdata = await workQuery.GetWorkData(id);
                if (sdata == null || sdata.StateName != AwaitingWorkState.Name)
                {
                    continue;
                }

                if (storage == null)
                {
                    await EnqueuedState(id, sdata.StateName, sdata);
                }
                else
                {
                    await EnqueuedState(storage, id, sdata.StateName, sdata);
                }
            }
        }

        /// <summary>
        /// 父任务已结束时把仍处于 awaiting 的子任务立即入队。
        /// 必须在子任务的创建事务<b>提交之后</b>调用：只有提交之后，
        /// "子任务注册"与"父任务扫描等待集合"两个事件才有确定的先后顺序；
        /// 否则子任务可能既没被父任务的扫描命中、自己检查时父任务的完成状态又尚未落库，
        /// 从而永久停留在 awaiting（丢失唤醒）。
        /// </summary>
        public static async Task<bool> EnqueueAwaitingWorkIfParentFinished(IWorkQuery workQuery,
            string workId,
            string parentId)
        {
            if (string.IsNullOrEmpty(parentId))
            {
                return false;
            }

            var data = await workQuery.GetWorkData(workId);
            if (data == null || data.StateName != AwaitingWorkState.Name)
            {
                return false;
            }

            if (!await ParentWorkFinished(workQuery, parentId))
            {
                return false;
            }

            await EnqueuedState(workId, data.StateName, data);
            return true;
        }

        public static async Task<string> CreateEnqueueWork(IWorkStorage storage, IWorkQueue workQueue,
            IWorkStateHandlerProvider provider,
            Workload workLoad,
            DateTime createTime,
            string trigger,
            IDictionary<string,object> parameters,
            string name)
        {
            var option = DependencyInjectionHelper.Get<WorkServerOption>();
            var queue = workLoad.Queue;
            if (string.IsNullOrEmpty(queue))
            {
                queue = option.Queues.First();
            }
            var state = new EnqueuedWorkState(string.Format(Resource.TriggerBy, trigger), queue);
            var workId = await CreateWork(storage, workQueue, provider, workLoad, createTime, trigger, state, parameters, name);
            return workId;
        }

        /// <param name="name">人工可识别的任务名（监控面板使用），所有创建路径的唯一收口处，必填</param>
        public static async Task<string> CreateWork(IWorkStorage storage, IWorkQueue workQueue,
            IWorkStateHandlerProvider provider,
            Workload workLoad,
            DateTime createTime,
            string trigger,
            IWorkState initState,
            IDictionary<string, object> parameters,
            string name)
        {
            Helper.CheckWorkName(name);

            // 队列在创建收口处统一解析：入队状态未指定队列时，依次回退负载队列与配置的首个队列。
            // 解析结果同时写回负载内容，任务失败重试、卡死重投（都以负载队列为准）才不会漂移到别的队列。
            if (initState is EnqueuedWorkState enqueuedState)
            {
                if (string.IsNullOrEmpty(enqueuedState.Queue))
                {
                    enqueuedState.Queue = !string.IsNullOrEmpty(workLoad.Queue)
                        ? workLoad.Queue
                        : DependencyInjectionHelper.Get<WorkServerOption>().Queues.First();
                }

                if (string.IsNullOrEmpty(workLoad.Queue))
                {
                    workLoad.Queue = enqueuedState.Queue;
                }
            }

            var workId = Helper.NewId();
            await storage.CreateWork(workId,workLoad, parameters, createTime, Helper.DefaultWorkExpireTime, name);
            var data = new WorkData
            {
                Content = workLoad.SerializeContent(),
                WorkId = workId,
                Name = name,
                CreatedUctTime = createTime,
                StateName = string.Empty,
                ExpireUtcTime = createTime.Add(Helper.DefaultWorkExpireTime)
            };

            foreach(var kv in parameters)
            {
                var str = Helper.Serialize(kv.Value);
                if (!string.IsNullOrEmpty(str)) {
                    data.Parameters.TryAdd(kv.Key,str);
                }
            }
            await ChangeState(storage, workId, initState,string.Empty,data);
            return workId;
        }

        public static async Task EnqueuedState(string workId
            ,string oldState
            ,WorkData data,string trigger="")
        {   

            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                await EnqueuedState(storage, workId, oldState, data,trigger);
                await storage.Commit();
            }
        }

        public static async Task EnqueuedState(IWorkStorage storage, string workId
            , string oldState
            , WorkData data, string trigger = "")
        {
            var option = DependencyInjectionHelper.Get<WorkServerOption>();
            var queue = option.Queues.First();
            if (data != null && !string.IsNullOrEmpty(data.Content.Queue))
            {
                queue = data.Content.Queue;
            }
            var astate = new EnqueuedWorkState(trigger, queue);
            await ChangeState(storage,workId, astate, oldState, data);
        }

        public static async Task ChangeState(string workId,IWorkState newState
            ,string oldState
            ,WorkData data)
        {
            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                await ChangeState(storage, workId, newState, oldState,data);
                await storage.Commit();
            }

            // 与执行进程一致：状态提交后再唤醒一次连续子任务，覆盖提交前扫描漏掉的子任务
            if (newState.Finish)
            {
                await EnqueueAwaitingChildren(DependencyInjectionHelper.Get<IWorkQuery>(), workId);
            }
        }

        public static async Task ChangeState(IWorkStorage storage,string workId, IWorkState newState
            , string oldState
            , WorkData data)
        {
            Workload work = Workload.Deserialize(data.Content);

            var handlerProvider = DependencyInjectionHelper.Get<IWorkStateHandlerProvider>();
            var workQueue = DependencyInjectionHelper.Get<IWorkQueue>();
            var ctx = new ChangeWorkStateContext(storage, workQueue, workId, newState, oldState,work,data);
            await handlerProvider.Run(ctx);
        }

        public static IWorkState ConvertState(EmptyWorkState emptyState)
        {
            IWorkState? state = null;
            switch (emptyState.StateName)
            {
                case EnqueuedWorkState.Name:
                    state = new EnqueuedWorkState(emptyState);
                    break;
                case AwaitingWorkState.Name:
                    state = new AwaitingWorkState(emptyState);
                    break;
                case DeletedWorkState.Name:
                    state = new DeletedWorkState(emptyState);
                    break;
                case FailedWorkState.Name:
                    state = new FailedWorkState(emptyState);
                    break;
                case ProcessingWorkState.Name:
                    state = new ProcessingWorkState(emptyState);
                    break;
                case ScheduledWorkState.Name:
                    state = new ScheduledWorkState(emptyState);
                    break;
                case SucceededWorkState.Name:
                    state = new SucceededWorkState(emptyState);
                    break;
            }

            if (state == null || string.IsNullOrEmpty(state.StateName)
                || state.StateName == EmptyWorkState.Name)
            {
                throw new Exception(string.Format(Resource.UnknownState, emptyState.StateName));
            }

            return state;
        }

        public static async Task<List<string>> GetCacheDatas(IWorkQuery workQuery,
            IDistributedLock @lock,
            string key,
            TimeSpan span,ILogger? logger=null)
        {
            try
            {
                var now = Helper.CurrentUtcTime();
                var timestamp = now.UnixMillisecondStamp();
                var delayTime = now.Add(span).UnixMillisecondStamp();
                using (await @lock.AcquireLock(key, Helper.DistributedLockTimeOut))
                {
                    // 每轮扫描上限取自配置（#22）
                    var ids = await workQuery.GetCacheSet(key, Helper.Zero, timestamp, DependencyInjectionHelper.Get<WorkServerOption>().GetBatchSize());
                    if (ids.Count > Helper.Zero)
                    {
                        using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
                        {
                            foreach (var id in ids)
                            {
                                await storage.UpdateCacheSet(key, delayTime, id);
                            }
                           await storage.Commit();
                        }
                    }
                    return ids;
                }
            }
            catch (LockBackendException ex)
            {
                // 取不到锁 = 本轮放弃扫描。修复前 logger 为空时这条分支完全静默，
                // 对外表现就是"什么都没做却不留痕迹"（§2.4-2）；这里补一个兜底 logger 并升到 Warning。
                var fallback = logger;
                if (fallback == null)
                {
                    var factory = DependencyInjectionHelper.Get<ILoggerFactory>(false);
                    fallback = factory?.CreateLogger(nameof(WorkFactory));
                }
                fallback?.LogWarning($"Skip {key} scan this round: cache set lock is held by another node. {ex.Message}");
                return new List<string>();
            }
        }
    }
}
