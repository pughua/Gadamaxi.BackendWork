using System;
using System.Collections.Generic;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Works.States;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 终态失败原因的提取契约。
    /// <para>
    /// 批量项（<c>BatchItem.Message</c>）与 saga 步骤（<c>SagaStep.Message</c>）的失败原因来自
    /// 同一个提取实现，它必须绕开两个"看起来该有值、实际永远为空"的来源：
    /// 执行链路构造 <see cref="FailedWorkState"/> 时 <c>Info</c> 传的是空串，而状态数据字典要到
    /// <c>Commit()</c> 才被惰性填充、管线此刻读到的也是空。
    /// 这条原因正是使用者排查失败、以及决定 saga 是否人工续补的唯一依据，不能为空。
    /// </para>
    /// </summary>
    public class FailureMessageTests
    {
        /// <summary>执行链路的真实形态：失败状态带强类型异常</summary>
        [Fact]
        public void 带强类型异常的失败状态_取异常消息()
        {
            var state = new FailedWorkState(
                new InvalidOperationException("业务根因：库存不足"), 12, "test-server", string.Empty);

            Assert.Equal("业务根因：库存不足", WorkCompletionResolver.ResolveFailureMessage(Context(state)));
        }

        /// <summary>状态对象从库里重构出来（拷贝构造）时 <c>Exception</c> 为 null，只能回退到状态数据</summary>
        [Fact]
        public void 从库重构的失败状态_回退到状态数据里的异常消息()
        {
            var persisted = new EmptyWorkState(FailedWorkState.Name, string.Empty, Helper.CurrentUtcTime(),
                new Dictionary<string, string> { { $"{nameof(FailedWorkState.Exception)}Message", "落库时的原因" } });

            var state = new FailedWorkState(persisted);

            Assert.Null(state.Exception);
            Assert.Equal("落库时的原因", WorkCompletionResolver.ResolveFailureMessage(Context(state)));
        }

        /// <summary>没有任何原因可用时必须安静返回空串，不能让回写管线抛异常把状态变更一起带崩</summary>
        [Fact]
        public void 两个来源都拿不到_返回空串且不抛异常()
        {
            var state = new FailedWorkState(new EmptyWorkState(
                FailedWorkState.Name, string.Empty, Helper.CurrentUtcTime(), new Dictionary<string, string>()));

            Assert.Equal(string.Empty, WorkCompletionResolver.ResolveFailureMessage(Context(state)));
        }

        private static ChangeWorkStateContext Context(IWorkState state)
            => new ChangeWorkStateContext(null!, null!, "work-under-test", state, null, null!, null!);
    }
}
