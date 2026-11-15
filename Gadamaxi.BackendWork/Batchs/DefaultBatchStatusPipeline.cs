using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Batchs
{
    internal class DefaultBatchStatusPipeline : IBatchStatusPipeline
    {
        private readonly WorkServerOption _option;
        private readonly IWorkQuery _query;
        public DefaultBatchStatusPipeline() 
        {
            _option = DependencyInjectionHelper.Get<WorkServerOption>();
            _query = DependencyInjectionHelper.Get<IWorkQuery>();
        }

        public async Task Handle(BatchChangeStatueContext ctx, Func<Task> next)
        {
            await BeforeNext(ctx);
            await next();
            await AfterNext(ctx);
        }

        private async Task BeforeNext(BatchChangeStatueContext ctx)
        {
            if (ctx.BatchWork.Status == BatchStatus.Awaiting)
            {
                var data = await _query.GetBatchById(ctx.BatchWork.ParentBatchId);
                if (data == null)
                {
                    ctx.BatchWork.Status = BatchStatus.Init;
                }
            }
        }

        private async Task AfterNext(BatchChangeStatueContext ctx)
        {
            if(ctx.BatchWork.Status == BatchStatus.Init)
            {
                await ctx.Storage.AddCacheSet(Helper.BatchKey,
                    Helper.CurrentUtcTime().UnixMillisecondStamp(),
                    ctx.BatchWork.BatchId);
            }

            if(ctx.BatchWork.Status == BatchStatus.Awaiting)
            {
                await ctx.Storage.AddCacheSet($"{Helper.BatchKey}:{ctx.BatchWork.ParentBatchId}",
                    Helper.CurrentUtcTime().UnixMillisecondStamp(),
                    ctx.BatchWork.BatchId);
            }

            if (ctx.OldStatus == BatchStatus.Awaiting)
            {
                await ctx.Storage.RemoveCacheSet($"{Helper.BatchKey}:{ctx.BatchWork.ParentBatchId}",
                    ctx.BatchWork.BatchId);
            }

            if(ctx.BatchWork.Finish())
            {
                await ctx.Storage.RemoveCacheSet(Helper.BatchKey,
                    ctx.BatchWork.BatchId);

                var ids = await _query.GetCacheSet($"{Helper.BatchKey}:{ctx.BatchWork.BatchId}",
                    Helper.Zero,
                    Helper.CurrentUtcTime().UnixMillisecondStamp(), int.MaxValue);
                foreach (var id in ids)
                {
                    await ctx.Storage.RemoveCacheSet($"{Helper.BatchKey}:{ctx.BatchWork.BatchId}", id);

                    //or use BatchFactory.ChangeStatus
                    await ctx.Storage.AddCacheSet(Helper.BatchKey,
                    Helper.CurrentUtcTime().UnixMillisecondStamp(),
                    id);
                }
            }
        }
    }
}
