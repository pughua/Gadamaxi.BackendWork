using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Storages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// saga 状态/游标推进的唯一收口（内部实现）。
    /// <para>
    /// 所有推进都遵循同一套语义：<b>先把期望值快照下来，再改内存对象，最后以 CAS 写库</b>。
    /// CAS 冲突时抛 <see cref="Exceptions.SagaConcurrentUpdateException"/> 回滚整个事务，
    /// 由 <see cref="WorkFactory.CommitWithCasGuard"/> 统一吞并成"已被别的推进者处理"。
    /// </para>
    /// 之所以把"推进 + 调度缓存动作"打包在三处（saga 进程 / 超时进程 / 管理 API）共用，
    /// 是因为游标推进与缓存动作必须在同一个事务里，分开写迟早会漏掉一半。
    /// </summary>
    internal static class SagaFactory
    {
        /// <summary>
        /// 推进游标 / 阶段，<b>不改动调度缓存</b>。
        /// 用于 saga 进程内部的推进：它本来就在调度缓存里，扫描器会把分数推后，无需额外写缓存。
        /// </summary>
        internal static Task<bool> Advance(IWorkStorage storage, ILogger? logger, SagaInstance saga,
            SagaStatus newStatus, int newCurrentIndex, int newCompensationIndex, string message)
        {
            return AdvanceInner(storage, logger, saga, newStatus, newCurrentIndex, newCompensationIndex, message,
                (stor) => Task.CompletedTask);
        }

        /// <summary>
        /// 推进到终态并把 saga 移出调度缓存：终态不再需要推进，留着只会让扫描器每轮空跑。
        /// </summary>
        internal static Task<bool> Finish(IWorkStorage storage, ILogger? logger, SagaInstance saga,
            SagaStatus newStatus, string message)
        {
            return AdvanceInner(storage, logger, saga, newStatus, saga.CurrentIndex, saga.CompensationIndex, message,
                async (stor) => await stor.RemoveCacheSet(Helper.SagaKey, saga.SagaId));
        }

        /// <summary>
        /// 推进并立刻唤醒调度缓存（分数置为当前时间），使该 saga 不必等满一个轮询周期。
        /// <para>
        /// 采用"先删除再登记"而不是 <c>UpdateCacheSet</c>：<c>UpdateCacheSet</c> 在缓存行不存在时
        /// 是静默无操作，而续补/取消这些路径可能来自"已被移出缓存的终态"，必须保证真的重新登记上
        /// （与 <c>BatchManager.Retry</c> 重新登记前先移除同一动机，避免撞上 (Type,Value) 唯一约束）。
        /// </para>
        /// </summary>
        internal static Task<bool> Wake(IWorkStorage storage, ILogger? logger, SagaInstance saga,
            SagaStatus newStatus, int newCurrentIndex, int newCompensationIndex, string message)
        {
            return AdvanceInner(storage, logger, saga, newStatus, newCurrentIndex, newCompensationIndex, message,
                async (stor) =>
                {
                    await stor.RemoveCacheSet(Helper.SagaKey, saga.SagaId);
                    await stor.AddCacheSet(Helper.SagaKey,
                        Helper.CurrentUtcTime().UnixMillisecondStamp(), saga.SagaId);
                });
        }

        private static async Task<bool> AdvanceInner(IWorkStorage storage, ILogger? logger, SagaInstance saga,
            SagaStatus newStatus, int newCurrentIndex, int newCompensationIndex, string message,
            Func<IWorkStorage, Task> afterCas)
        {
            // 期望值必须先快照：下面会改内存对象，改完再取期望值就等于"期望当前值"，CAS 永远成立
            var expectedStatus = saga.Status;
            var expectedCurrentIndex = saga.CurrentIndex;
            var expectedCompensationIndex = saga.CompensationIndex;

            saga.Status = newStatus;
            saga.CurrentIndex = newCurrentIndex;
            saga.CompensationIndex = newCompensationIndex;
            if (!string.IsNullOrEmpty(message))
            {
                saga.Message = message;
            }

            return await WorkFactory.CommitWithCasGuard(storage, logger, async () =>
            {
                await storage.AdvanceSaga(saga, expectedStatus, expectedCurrentIndex, expectedCompensationIndex);
                await afterCas(storage);
            }, $"Saga {saga.SagaId} advance skipped.");
        }
    }
}
