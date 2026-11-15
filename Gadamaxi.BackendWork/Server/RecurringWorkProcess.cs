using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Recurrings;
using Gadamaxi.BackendWork.Resources;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works;
using Gadamaxi.BackendWork.Works.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    internal class RecurringWorkProcess : IOnceWorkProcess,IDisposable
    {
        private readonly ILogger<RecurringWorkProcess> _logger;
        private readonly IDistributedLock _distributedLock;
        private readonly IWorkQuery _workQuery;
        private readonly IWorkStateHandlerProvider _handlerProvider;
        private readonly IWorkQueue _workQueue;
        private readonly WorkServerOption _option;
        private Timer? _timer;
        // async-void 的 Timer 回调可能被同一 Timer 再次触发而重叠执行；
        // 用原子标志防止重叠，避免重复拉取与重复触发（#11 防御性加固）。
        private int _running = Helper.Zero;

        public RecurringWorkProcess(ILogger<RecurringWorkProcess> logger)
        {
            _logger = logger;
            _distributedLock = DependencyInjectionHelper.Get<IDistributedLock>();
            _workQuery = DependencyInjectionHelper.Get<IWorkQuery>();
            _handlerProvider = new WorkStateHandlerProvider();
            _workQueue = DependencyInjectionHelper.Get<IWorkQueue>();
            _option = DependencyInjectionHelper.Get<WorkServerOption>();
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }

        public Task Execute(WorkProcessContext context)
        {
            if (_timer != null)
            {
                return Task.CompletedTask;
            }

            // 线程可能在停止令牌已取消之后才被创建（派发是逐个启动线程），
            // 此时不能再启动定时触发，否则停机后仍在产生新的周期任务（#9）
            if (context.Server.StopToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            var second = Helper.CurrentUtcTime().Second;
            var due = TimeSpan.FromSeconds(Helper.MinutePerSecond - second);

            _timer = new Timer(async (obj) =>
            {
                if(obj is WorkProcessContext ctx)
                {
                    await TimerStart(ctx);
                }
            }, context,due, _option.RecurringPollingDelay);

            // 定时器回调不属于任何工作线程的循环，取消停止令牌并不会让它停下：
            // 停机时释放定时器，避免服务已停止却仍在触发周期任务（#9）
            context.Server.StopToken.Register(() =>
            {
                try
                {
                    Dispose();
                }
                catch
                {
                    // 停机路径不因释放失败而抛出
                }
            });

            return Task.CompletedTask;
        }

        private async Task TimerStart(WorkProcessContext context)
        {
            // 已停机：不再处理新的周期触发
            if (context.Server.StopToken.IsCancellationRequested)
            {
                return;
            }

            // 防止 async-void 回调重叠：若上一次仍在运行，直接返回。
            if (Interlocked.CompareExchange(ref _running, Helper.One, Helper.Zero) != Helper.Zero)
            {
                return;
            }

            try
            {
                Helper.CheckArgumentNull(context, nameof(context));
                var count = Helper.Zero;
                do
                {
                    count = await EnqueueRecurringWork(context);
                }
                while (count > Helper.Zero);

                // 注意：此处不能再做 RecurringPollingDelay 的额外等待——
                // Timer 本身已按该周期重复触发，回调内再等一个周期会让回调时长 ≥ Timer 周期，
                // 下一个 tick 因 _running 防重叠守卫被跳过，单节点实际退化为每两个周期触发一次（实测复现）。
                // do-while 已保证积压批量（> BatchSize）在本轮回调内消化完毕。
            }
            catch(Exception ex)
            {
                _logger.LogWarning($"EnqueueRecurring error.{ex.Message}.\n{ex.GetStacks()}");
            }
            finally
            {
                Interlocked.Exchange(ref _running, Helper.Zero);
            }
        }

        private async Task<int> EnqueueRecurringWork(WorkProcessContext context)
        {
            var ids = await WorkFactory.GetCacheDatas(_workQuery, _distributedLock, Helper.RecurringKey, _option.RecurringPollingDelay,_logger);
            _logger.LogTrace($"Fetch {ids.Count} RecurringWork.");
            foreach (var id in ids)
            {
                var retryCount = 0;
                var ex = await Helper.Retry(async (idx) =>
                {
                    retryCount = idx;
                    await TryEnqueueRecurringWork(id);
                }, Helper.DefaultRetryCount, Helper.DefaultRetryDelay);
                if (ex != null)
                {
                    _logger.LogWarning($"RecurringWork {id} trigger failure.Try again{retryCount} times. {ex.Message}\n{ex.GetStacks()}");
                }
                else
                {
                    await Task.Delay(Helper.DefaultWorkIntervalDelay);
                }
            }

            return ids.Count;

        }

        private async Task TryEnqueueRecurringWork(string id)
        {
            using (var storage = DependencyInjectionHelper.Get<IWorkStorage>())
            {
                // CAS 抢占失败 = 该次触发已被其它触发者处理，属正常并发去重而非故障：
                // 守卫统一负责"回滚 + 记 Info 日志 + 返回 false"，这里只需据此决定是否记成功日志
                var committed = await WorkFactory.CommitWithCasGuard(storage, _logger,
                    async () => await WorkFactory.TriggerRecurringWork(storage, _workQuery, _workQueue, _handlerProvider, id, this.GetType().Name, _logger),
                    $"RecurringWork {id} duplicate trigger skipped.");

                if (committed)
                {
                    _logger.LogInformation($"RecurringWork {id} trigger success.");
                }
            }
        }
    }
}
