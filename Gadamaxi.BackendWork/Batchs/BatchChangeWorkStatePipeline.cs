using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Batchs
{
    public class BatchChangeWorkStatePipeline : IChangeStatePipeline
    {
        private readonly IWorkQuery _workQuery;
        public BatchChangeWorkStatePipeline(IWorkQuery workQuery)
        {
            _workQuery = workQuery;
        }

        public async Task<IWorkState> Handle(ChangeWorkStateContext context, Func<Task<IWorkState>> next)
        {
            var result = await next();

            var batchId = context.WorkData.Parameters.GetValueOrDefault(Helper.BatchIdKey);
            var batchItemId = context.WorkData.Parameters.GetValueOrDefault(Helper.BatchItemIdKey);
            if (!string.IsNullOrEmpty(batchId) && !string.IsNullOrEmpty(batchItemId))
            {
                var status = await ResolveItemStatus(context);
                if (status != null)
                {
                    // 失败原因必须走共享提取（强类型 Exception），不能取 NewState.Info：
                    // 执行链路构造 FailedWorkState 时 Info 传的是空串，而 GetData 的数据字典
                    // 要到 Commit() 才填充，管线此刻读到的是空 —— 批量项的失败原因会整个丢掉。
                    await context.Storage.UpdateBatchItem(batchId, batchItemId, status.Value,
                        status.Value == BatchStatus.Failure
                            ? WorkCompletionResolver.ResolveFailureMessage(context)
                            : string.Empty);
                }
            }
            return result;
        }

        /// <summary>
        /// 子工作到达终态时才回写批量项状态；仍在重试链路中的失败不回写，批量项保持 Processing。
        /// 「完成结论」的判定与 saga 步骤回写共用 <see cref="WorkCompletionResolver"/>，两处口径必然一致。
        /// </summary>
        private async Task<BatchStatus?> ResolveItemStatus(ChangeWorkStateContext context)
        {
            var completion = await WorkCompletionResolver.Resolve(_workQuery, context);
            switch (completion)
            {
                case WorkCompletion.Succeeded:
                    return BatchStatus.Success;
                case WorkCompletion.Failed:
                    return BatchStatus.Failure;
                default:
                    return null;
            }
        }
    }
}
