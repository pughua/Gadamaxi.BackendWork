using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Exceptions
{
    /// <summary>
    /// saga 推进的 CAS 抢占失败：期望的状态 / 游标已被其它推进者改过。
    /// <para>
    /// 存储写入是延迟事务（<c>Commit()</c> 才落库），拿不到同步的受影响行数，
    /// 因此以"0 行受影响 → 抛异常"来中止并回滚整个推进事务（与 #11 周期任务触发同一套做法）。
    /// 调用方应把它视作"该次推进已被别的推进者处理"，属正常并发去重而非故障。
    /// </para>
    /// </summary>
    public class SagaConcurrentUpdateException : BackendException
    {
        public SagaConcurrentUpdateException(string message) : base(message)
        {
        }
    }
}
