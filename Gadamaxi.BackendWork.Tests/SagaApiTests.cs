using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Sagas;
using LinqToDB;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// saga 的 API 契约层：创建落库（含实例还原数据）、命名/业务键校验、启动期校验（D6）、
    /// 不可补偿步骤、以及取消 / 续补的状态前置校验。这一层不驱动调度，
    /// 只看"说出来的东西有没有正确落到库里"。
    /// </summary>
    public class SagaApiTests : SagaTestBase
    {
        [Fact]
        public async Task 创建saga_落库包含实例快照与全部步骤()
        {
            var sagaId = await Saga.Start(new ScriptSaga
            {
                Corr = "order-A",
                SagaName = "订单履约",
                StepCount = 2,
                InitialVariables = new Dictionary<string, string> { ["orderId"] = "order-A" }
            });

            var saga = await GetSaga(sagaId);
            Assert.NotNull(saga);
            Assert.Equal("订单履约", saga!.Name);
            Assert.Equal("order-A", saga.CorrelationId);
            Assert.Equal(SagaStatus.None, saga.Status);
            Assert.Equal(2, saga.StepCount);
            // 正向游标从 1 起、补偿游标为 0（未进入补偿）
            Assert.Equal(1, saga.CurrentIndex);
            Assert.Equal(0, saga.CompensationIndex);
            // 实例还原数据（D2=B）：实现类定位 + 输入快照
            Assert.Contains("ScriptSaga", saga.SagaType);
            Assert.Contains("order-A", saga.InputJson);
            // 变量池：Configure 里 SetVariable 的初始值已落库
            Assert.Equal("order-A", saga.Variables["orderId"]);

            var steps = await GetSteps(sagaId);
            Assert.Equal(2, steps.Count);
            // 步骤按序号升序返回，名字默认取正向方法名（D4）
            Assert.Equal(new[] { 1, 2 }, steps.Select(t => t.Index).ToArray());
            Assert.Equal(new[] { "Forward1", "Forward2" }, steps.Select(t => t.Name).ToArray());
            Assert.All(steps, t => Assert.Equal(SagaStepStatus.Init, t.Status));
            Assert.All(steps, t => Assert.True(t.HasCompensate()));
            Assert.Equal("Forward1", steps[0].ForwardMethod);
            Assert.Equal("Compensate1", steps[0].CompensateMethod);

            // 创建即登记调度缓存，否则 saga 永远不会被推进
            Assert.True(await Query.CheckCacheSet(Helper.SagaKey, sagaId));
        }

        [Fact]
        public async Task 创建saga_同一业务键重复启动_返回既有实例()
        {
            var first = await Saga.Start(new ScriptSaga { Corr = "order-B", SagaName = "幂等创建", StepCount = 1 });
            var second = await Saga.Start(new ScriptSaga { Corr = "order-B", SagaName = "幂等创建", StepCount = 1 });

            Assert.Equal(first, second);

            Assert.Equal(1, await Db.CountSagasAsync("order-B"));
        }

        [Fact]
        public async Task 创建saga_名称为空或超长_直接拒绝()
        {
            // 空名
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                Saga.Start(new ScriptSaga { Corr = "order-C", SagaName = "", StepCount = 1 }));

            // saga 名上限比普通任务名更小：补偿工作名要追加 "~undo" 后缀
            var tooLong = new string('x', Helper.MaxSagaNameLength + 1);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                Saga.Start(new ScriptSaga { Corr = "order-D", SagaName = tooLong, StepCount = 1 }));
        }

        [Fact]
        public async Task 创建saga_业务键为空_直接拒绝()
        {
            // CorrelationId 是唯一索引与幂等创建的键，为空会让所有 saga 挤到同一个键上
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                Saga.Start(new ScriptSaga { Corr = "", StepCount = 1 }));
        }

        [Fact]
        public async Task 创建saga_没有步骤_直接拒绝()
        {
            await Assert.ThrowsAsync<BackendException>(() =>
                Saga.Start(new ScriptSaga { Corr = "order-E", StepCount = 0 }));
        }

        [Fact]
        public async Task 创建saga_不可补偿步骤_补偿方法为空()
        {
            var sagaId = await Saga.Start(new ScriptSaga
            {
                Corr = "order-F",
                StepCount = 2,
                NoCompensateAt = 2
            });

            var steps = await GetSteps(sagaId);
            Assert.True(steps[0].HasCompensate());
            // 空串即"该步骤不可补偿"，回滚时会被记为 Skipped 并留痕
            Assert.False(steps[1].HasCompensate());
            Assert.Equal(string.Empty, steps[1].CompensateMethod);
        }

        [Fact]
        public async Task 创建saga_自定义步骤名_按原样使用()
        {
            var sagaId = await Saga.Start(new NamedStepSaga { Corr = "order-G" });

            var steps = await GetSteps(sagaId);
            Assert.Equal("冻结库存", steps[0].Name);
            // 补偿名在步骤名后加 "~undo"
            Assert.Equal("冻结库存~undo", steps[0].CompensateName());
        }

        #region 启动期校验（D6）

        [Fact]
        public async Task 校验V1_步骤不是本类方法_启动即拒绝()
        {
            // 表达式 s => SagaTestJobs.StaticJob() 编译能过（参数未用），
            // 但方法不在实现类上 —— 必须在 Start 时拦住，而不是执行期才失败
            await Assert.ThrowsAsync<BackendException>(() => Saga.Start(new V1Saga()));
        }

        [Fact]
        public async Task 校验V2撇_声明处传实参_启动即拒绝()
        {
            // 参数按名绑定，声明处传实参意味着"两处绑定源"
            await Assert.ThrowsAsync<BackendException>(() => Saga.Start(new V2PrimeSaga()));
        }

        [Fact]
        public async Task 校验V2双撇_参数绑不到源_启动即拒绝()
        {
            // 方法参数 missing 在输入属性 / 变量池 / outputKey 里都找不到
            await Assert.ThrowsAsync<BackendException>(() => Saga.Start(new V2DoublePrimeSaga()));
        }

        [Fact]
        public async Task 校验V4_既未注册DI也无无参构造_启动即拒绝()
        {
            // 执行期 InstanceFactory 建不出实例，必须在启动时挡住
            await Assert.ThrowsAsync<BackendException>(() => Saga.Start(new V4Saga(1)));
        }

        [Fact]
        public async Task 校验V5_步骤名重复_启动即拒绝()
        {
            await Assert.ThrowsAsync<BackendException>(() => Saga.Start(new V5Saga()));
        }

        [Fact]
        public async Task 校验V6_输入不可序列化_启动即拒绝()
        {
            // 自引用属性让 JSON 序列化抛 ReferenceLoop 异常
            var saga = new V6Saga();
            saga.Self = saga;
            await Assert.ThrowsAsync<BackendException>(() => Saga.Start(saga));
        }

        [Fact]
        public async Task 校验V7_outputKey重复_启动即拒绝()
        {
            // 变量池会互相覆盖
            await Assert.ThrowsAsync<BackendException>(() => Saga.Start(new V7Saga()));
        }

        #endregion

        [Fact]
        public async Task 取消_终态saga_不做任何改动()
        {
            var sagaId = await CreateTwoStepSaga("order-H");

            // 先跑到正向成功（终态 Completed）
            Assert.True(await DriveUntilFinish(sagaId), "saga 应在超时前完成");
            Assert.Equal(SagaStatus.Completed, (await GetSaga(sagaId))!.Status);

            Assert.False(await Saga.Cancel(sagaId, "用户取消"));
            Assert.Equal(SagaStatus.Completed, (await GetSaga(sagaId))!.Status);
        }

        [Fact]
        public async Task 取消_运行中的saga_转入补偿且记录原因()
        {
            var sagaId = await CreateTwoStepSaga("order-I");

            // 只推进游标、不执行步骤工作：saga 会停在"第一步已展开、正向进行中"
            var running = await WaitUntil(async () =>
            {
                await SagaProcess.Execute(Context);
                var current = await GetSaga(sagaId);
                return current != null && current.Status == SagaStatus.Running;
            });
            Assert.True(running, "saga 应进入 Running");

            var beforeCancel = await GetSaga(sagaId);
            Assert.Equal(1, beforeCancel!.CurrentIndex);

            Assert.True(await Saga.Cancel(sagaId, "用户取消"));

            var canceled = await GetSaga(sagaId);
            Assert.Equal(SagaStatus.Compensating, canceled!.Status);
            // 取消走与"正向失败"相同的回滚路径：补偿从失败/取消步骤的前一步开始
            Assert.Equal(0, canceled.CompensationIndex);
            Assert.Contains("canceled", canceled.Message);
        }

        [Fact]
        public async Task 续补_非Aborted的saga_直接拒绝()
        {
            var sagaId = await CreateTwoStepSaga("order-J");
            await Assert.ThrowsAsync<BackendException>(() => Saga.RetryCompensation(sagaId));
        }

        [Fact]
        public async Task 续补_saga不存在_直接拒绝()
        {
            await Assert.ThrowsAsync<BackendException>(() => Saga.RetryCompensation("not-exist-saga"));
        }

        private Task<string> CreateTwoStepSaga(string correlationId)
        {
            return Saga.Start(new ScriptSaga
            {
                Corr = correlationId,
                SagaName = "两步流程",
                StepCount = 2
            });
        }
    }

    #region 启动期校验用例的实现类

    /// <summary>自定义步骤名的用例类（须有 public 无参构造：执行期还原实例用，V4）</summary>
    public class NamedStepSaga : Saga<NamedStepSaga>
    {
        public string Corr { get; set; } = string.Empty;
        public string Sku { get; set; } = string.Empty;

        public override string CorrelationId => Corr;

        protected override void Configure(SagaDefinition<NamedStepSaga> def)
        {
            def.Step(s => s.Reserve(), s => s.Release(), name: "冻结库存");
        }

        public Task Reserve()
        {
            SagaTestJobs.Record("n1");
            return Task.CompletedTask;
        }

        public Task Release()
        {
            SagaTestJobs.Record("nu1");
            return Task.CompletedTask;
        }
    }

    /// <summary>V1：步骤声明不在实现类上</summary>
    public class V1Saga : Saga<V1Saga>
    {
        public override string CorrelationId => "v1-saga";

        protected override void Configure(SagaDefinition<V1Saga> def)
        {
            def.Step(_ => SagaTestJobs.StaticJob(), _ => SagaTestJobs.StaticJob());
        }
    }

    /// <summary>V2'：声明处传了实参（参数必须按名绑定）</summary>
    public class V2PrimeSaga : Saga<V2PrimeSaga>
    {
        public override string CorrelationId => "v2p-saga";

        protected override void Configure(SagaDefinition<V2PrimeSaga> def)
        {
            def.Step(s => s.Do("arg"), s => s.Undo());
        }

        public Task Do(string value)
        {
            return Task.CompletedTask;
        }

        public Task Undo()
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// V2''：方法参数绑不到任何源（无同名属性 / 变量 / outputKey）。
    /// 参数 <c>missing</c> 与类里任何成员都不同名 —— 声明处注记的 <c>s.Sku</c> 只影响编译，
    /// 绑定严格按参数名解析，因此启动校验会拒绝。
    /// </summary>
    public class V2DoublePrimeSaga : Saga<V2DoublePrimeSaga>
    {
        public string Sku { get; set; } = string.Empty;

        public override string CorrelationId => "v2pp-saga";

        protected override void Configure(SagaDefinition<V2DoublePrimeSaga> def)
        {
            def.Step(s => s.Do(s.Sku), s => s.Undo());
        }

        public Task Do(string missing)
        {
            return Task.CompletedTask;
        }

        public Task Undo()
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>V4：既没注册 DI 也没有 public 无参构造</summary>
    public class V4Saga : Saga<V4Saga>
    {
        public V4Saga(int value)
        {
            Value = value;
        }

        private int Value { get; }

        public override string CorrelationId => "v4-saga";

        protected override void Configure(SagaDefinition<V4Saga> def)
        {
            def.Step(s => s.Do(), s => s.Undo());
        }

        public Task Do()
        {
            return Task.CompletedTask;
        }

        public Task Undo()
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>V5：步骤名重复</summary>
    public class V5Saga : Saga<V5Saga>
    {
        public override string CorrelationId => "v5-saga";

        protected override void Configure(SagaDefinition<V5Saga> def)
        {
            def.Step(s => s.Do(), s => s.Undo(), name: "same");
            def.Step(s => s.DoAgain(), s => s.Undo(), name: "same");
        }

        public Task Do()
        {
            return Task.CompletedTask;
        }

        public Task DoAgain()
        {
            return Task.CompletedTask;
        }

        public Task Undo()
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>V6：输入快照不可序列化（自引用导致 JSON 序列化失败）</summary>
    public class V6Saga : Saga<V6Saga>
    {
        public V6Saga? Self { get; set; }

        public override string CorrelationId => "v6-saga";

        protected override void Configure(SagaDefinition<V6Saga> def)
        {
            def.Step(s => s.Do(), s => s.Undo());
        }

        public Task Do()
        {
            return Task.CompletedTask;
        }

        public Task Undo()
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>V7：outputKey 重复（变量池互相覆盖）</summary>
    public class V7Saga : Saga<V7Saga>
    {
        public override string CorrelationId => "v7-saga";

        protected override void Configure(SagaDefinition<V7Saga> def)
        {
            def.Step(s => s.Do(), s => s.Undo(), outputKey: "k");
            def.Step(s => s.DoAgain(), s => s.Undo(), outputKey: "k");
        }

        public Task Do()
        {
            return Task.CompletedTask;
        }

        public Task DoAgain()
        {
            return Task.CompletedTask;
        }

        public Task Undo()
        {
            return Task.CompletedTask;
        }
    }

    #endregion
}
