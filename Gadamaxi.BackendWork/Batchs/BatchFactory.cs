using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Batchs
{
    internal class BatchFactory
    {
        internal static async Task ChangeStatus(IWorkStorage storage
            , BaseBatchWork batchWork
            , BatchStatus newStatus
            , string message)
        {
            if (batchWork.Status == BatchStatus.Failure 
                && newStatus == BatchStatus.Failure)
            {
                batchWork.RetryCount = batchWork.RetryCount + Helper.One;
            }

            var oldstatue = batchWork.Status;
            batchWork.Status = newStatus;
            if (!string.IsNullOrEmpty(message))
            {
                batchWork.Message = message;
            }
            var ctx = new BatchChangeStatueContext(storage,batchWork,oldstatue);

            await ChangeStatus(ctx);
        }

        private static async Task ChangeStatus(BatchChangeStatueContext ctx)
        {
            var provider = DependencyInjectionHelper.Get<IBatchStatusPipelineProvider>();
            Func<Task> next = async () =>
            {
                await ctx.Storage.UpdateBatch(ctx.BatchWork);
            };

            Func<Func<Task>, Func<Task>> pred;
            foreach (var pip in provider.GetPipelines().Reverse())
            {
                pred = (nxt) =>
                {
                    return async () =>
                    {
                        await pip.Handle(ctx, nxt);
                    };
                };

                next = pred(next);
            }

            await next();
        }
    }
}
