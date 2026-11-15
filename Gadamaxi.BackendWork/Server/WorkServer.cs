using Gadamaxi.BackendWork.Storages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Server
{
    public class WorkServer : IWorkServer
    {
        private readonly WorkServerOption _option;
        private readonly ILogger<WorkServer> _logger;
        private readonly CancellationTokenSource _stoppingCts = new CancellationTokenSource();
        private readonly IWorkDispatcher _workDispatcher;
        private readonly IWorkExecution _workExecution;
        private readonly IWorkServerProcess _serverProcess;
        /// <summary>运行期上下文，停机时用于读取在途任务数（#9）</summary>
        private ServerContext? _context;
        int _disposed;

        /// <summary>
        /// 协作组件全部由 DI 提供（#15）：修复前 WorkExecution/WorkDispatcher/WorkServerProcess
        /// 都在本构造函数里 internal new，既不可替换也无法单测。
        /// 配置走 Options Pattern（§2.4-4），由 <see cref="IOptions{WorkServerOption}"/> 取值。
        /// </summary>
        public WorkServer(IOptions<WorkServerOption> options,
            ILoggerFactory loggerFactory,
            IWorkExecution workExecution,
            IWorkDispatcher workDispatcher,
            IWorkServerProcess serverProcess)
        {
            Helper.CheckArgumentNull(options, nameof(options));
            _option = options.Value;
            _logger = loggerFactory.CreateLogger<WorkServer>();
            _workExecution = workExecution;
            _workDispatcher = workDispatcher;
            _serverProcess = serverProcess;

            if(_option.Queues==null || _option.Queues.Count <= 0)
            {
                Helper.CheckMiniValueError(Helper.One, Helper.Zero, nameof(_option.Queues));
            }
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _disposed) == Helper.One)
            {
                return;
            }
            if (Interlocked.Exchange(ref _disposed, Helper.One) == Helper.One)
            {
                return;
            }
            _stoppingCts.Dispose();
        }

        public async Task Run()
        {
            if (!_option.UseBackendServer)
            {
                throw new Exception("Disable backend services in configuration");
            }
            var context = new ServerContext(_stoppingCts.Token, _workDispatcher,new List<IWorkProcess>().AsReadOnly());
            _context = context;
            context.Queues = _option.Queues;
            // 修复前 WorkThreads 从未被赋值（恒 0），线程数只能走"自动推算"分支（#15）
            context.WorkThreads = _option.WorkerCount;
            if (!string.IsNullOrEmpty(_option.ServerName))
            {
                context.ServerName = _option.ServerName;
            }
            else
            {
                context.ServerName = $"{Environment.MachineName}:{Helper.NewId()}";
            }
            // 回写最终节点名：出队抢占标记（Queue.FetchedBy）以它做前缀，
            // 保证"谁取走的"与集群节点列表里的 ServerName 是同一个名字（否则面板两侧显示不一致、统计对不上）
            _option.ServerName = context.ServerName;
            _logger.LogInformation($"Starting server {context.ServerName}");
            await _workDispatcher.Dispatch(context, _serverProcess,Helper.One);
        }

        /// <summary>
        /// 优雅停机（#9），协议为"停取新任务 → 等在途任务 → 超时放弃"：
        /// 1. 取消停止令牌：各进程循环与轮询等待都响应它，因此不再取新的任务；
        /// 2. 等待在途任务：工作线程跑完当前任务后自行退出，这里等所有线程真正退出；
        /// 3. 超时放弃：超过 <see cref="WorkServerOption.ShutdownTimeout"/> 不再等待。
        ///    任务方法不可取消（#13 未实施），无法真正"强杀"；工作线程都是后台线程，
        ///    进程仍可正常退出，超时只意味着这部分在途工作留待可见性超时后重新投递。
        /// </summary>
        public async Task Stop()
        {
            if (Volatile.Read(ref _disposed) == Helper.One)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }

            // 1) 停止取新任务
            _stoppingCts.Cancel();

            var timeout = _option.ShutdownTimeout;
            if (timeout <= TimeSpan.Zero)
            {
                // 配置为 0 表示不等待在途任务，退化为立即停机
                return;
            }

            // 2) 等待在途任务跑完（工作线程退出）
            var exited = await _workDispatcher.WaitForExitAsync(timeout);

            // 3) 超时：记录仍在途的任务数后放弃等待
            if (!exited)
            {
                _logger.LogWarning($"Graceful shutdown timed out after {timeout}; " +
                    $"{_context?.InFlightCount ?? 0} work(s) still in flight, abandoning them.");
            }
        }

        /// <summary>兼容旧命名（StartStop）：请改用 <see cref="Stop"/>。</summary>
        [Obsolete("命名修正：请改用 Stop()。")]
        public Task StartStop() => Stop();
    }
}
