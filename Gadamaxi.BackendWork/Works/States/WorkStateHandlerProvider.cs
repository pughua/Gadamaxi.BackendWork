using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Works.States
{
    public class WorkStateHandlerProvider: IWorkStateHandlerProvider
    {
        private readonly List<IWorkStateHandler> _innerHandlers;
        private readonly List<IChangeStatePipeline> _changeStatePipelines;
        private readonly ILogger<WorkStateHandlerProvider> _logger;
        public WorkStateHandlerProvider() 
        {
            _innerHandlers = new List<IWorkStateHandler>()
            {
                new EnqueuedWorkStateHandler(),
                new AwaitingWorkStateHandler(),
                new DeletedWorkStateHandler(),
                new FailedWorkStateHandler(),
                new ProcessingWorkStateHandler(),
                new SucceededWorkStateHandler(),
                new ScheduledWorkStateHandler()
            };

            _changeStatePipelines = new List<IChangeStatePipeline>()
            {
                new DefaultChangeStatePipeline()
            };

            var pips = DependencyInjectionHelper.Get<IEnumerable<IChangeStatePipeline>>(false);
            if (pips != null)
            {
                _changeStatePipelines.AddRange(pips);
            }

            var loggerFactory = DependencyInjectionHelper.Get<ILoggerFactory>();
            _logger = loggerFactory.CreateLogger<WorkStateHandlerProvider>();

        }
        public async Task<IWorkState> Run(ChangeWorkStateContext context)
        {
            Func<Task<IWorkState>> next = async () =>
            {
                return await InnerRun(context);
            };

            Func<Func<Task<IWorkState>>, Func<Task<IWorkState>>> pred;
            foreach (var pip in _changeStatePipelines.AsEnumerable().Reverse())
            {
                pred = (nxt) =>
                {
                    return async () =>
                    {
                        return await pip.Handle(context, nxt);
                    };
                };

                next = pred(next);
            }

            var result = await next();

            return result;
        }

        private async Task<IWorkState> InnerRun(ChangeWorkStateContext context)
        {
            await ChangeState(context);

            var handlers = GetHandlers(context.NewState.StateName);
            foreach (var handler in handlers)
            {
                await handler.Handle(context);
            }

            if (!string.IsNullOrEmpty(context.OldStateName))
            {
                var oldhandlers = GetHandlers(context.OldStateName);
                foreach (var handler in oldhandlers)
                {
                    await handler.RemovedHandle(context);
                }
            }

            return context.NewState;
        }

        private async Task ChangeState(ChangeWorkStateContext context)
        {
            if (context.NewState is EnqueuedWorkState state)
            {
                if (string.IsNullOrEmpty(state.Queue))
                {
                    var option = DependencyInjectionHelper.Get<WorkServerOption>();
                    state.Queue = option.Queues.First();
                }
            }
            await context.Storage.SetWorkState(context.WorkId, context.NewState);

            _logger.LogInformation($"Work {context.WorkId} change state to {context.NewState.StateName}.");
        }

        private IEnumerable<IWorkStateHandler> GetHandlers(string stateName)
        {
            Helper.CheckArgumentNull(stateName, nameof(stateName));
            return _innerHandlers.Where(s=>s.StateName==stateName);
        }
    }
}
