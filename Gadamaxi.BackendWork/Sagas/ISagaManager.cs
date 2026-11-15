using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Sagas
{
    /// <summary>
    /// saga 的创建与管理入口。
    /// <para>
    /// 编排推进本身由 <c>SagaWorkProcess</c> 自动完成，本接口只负责「启动 / 查询 / 人工干预」三类操作。
    /// </para>
    /// </summary>
    public interface ISagaManager
    {
        /// <summary>
        /// 创建并启动一个 saga。流程定义来自 <typeparamref name="TSaga"/> 的
        /// <c>Configure</c>（步骤与补偿都声明在实现类里），业务输入随实例快照落库。
        /// </summary>
        /// <param name="saga">
        /// 调用方 <c>new</c> 出来并填好业务输入的 saga 实例。其 <see cref="ISaga.CorrelationId"/>
        /// 是业务关联键，<b>必填且唯一</b>：重复启动同一业务键会返回既有 SagaId（幂等创建）。
        /// </param>
        /// <returns>saga Id（幂等命中时返回既有 Id）</returns>
        Task<string> Start<TSaga>(TSaga saga) where TSaga : Saga<TSaga>;

        /// <summary>
        /// 人工取消：正在执行或尚未开始的 saga 转补偿（走与失败同一条回滚路径），终态落 <see cref="SagaStatus.Compensated"/>。
        /// 已在补偿中或已到终态的 saga 不会被改动。
        /// </summary>
        /// <returns>是否真的发起了取消</returns>
        Task<bool> Cancel(string sagaId, string reason = "");

        /// <summary>
        /// 补偿失败（<see cref="SagaStatus.Aborted"/>）后人工续补：补偿游标保持不变，只重试失败的那一步补偿。
        /// </summary>
        /// <exception cref="Exceptions.BackendException">saga 不存在，或当前状态不是 Aborted</exception>
        Task RetryCompensation(string sagaId);

        Task<SagaInstance?> GetSaga(string sagaId);

        Task<SagaInstance?> GetSagaByCorrelationId(string correlationId);

        /// <summary>读取全部步骤（按序号升序）</summary>
        Task<List<SagaStep>> GetSteps(string sagaId);
    }
}
