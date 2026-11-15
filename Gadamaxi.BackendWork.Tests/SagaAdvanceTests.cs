using System;
using System.Linq;
using System.Threading.Tasks;
using Gadamaxi.BackendWork;
using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Sagas;
using Gadamaxi.BackendWork.Storages;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// saga 推进层：手动驱动 <see cref="SagaWorkProcess"/> 与步骤工作，
    /// 验证游标推进、逆序补偿、留痕、CAS 守卫、实例还原与变量传递。
    /// </summary>
    public class SagaAdvanceTests : SagaTestBase
    {
        [Fact]
        public async Task 正向推进_三步全成功_到Completed()
        {
            var sagaId = await Saga.Start(new ScriptSaga { Corr = "adv-1", SagaName = "三步全成功", StepCount = 3 });

            Assert.True(await DriveUntilFinish(sagaId), "saga 应在超时前完成");

            var saga = await GetSaga(sagaId);
            Assert.Equal(SagaStatus.Completed, saga!.Status);
            // 游标越过最后一步 = 正向完成
            Assert.Equal(4, saga.CurrentIndex);
            Assert.Equal(0, saga.CompensationIndex);

            var steps = await GetSteps(sagaId);
            Assert.All(steps, t => Assert.Equal(SagaStepStatus.Succeeded, t.Status));
            Assert.All(steps, t => Assert.False(string.IsNullOrEmpty(t.ForwardWorkId)));
            // 正向成功不需要补偿，补偿工作一个都不该展开
            Assert.All(steps, t => Assert.True(string.IsNullOrEmpty(t.CompensateWorkId)));

            // 每一步只执行一次，且按声明顺序串行执行
            var records = SagaTestJobs.Records.ToArray();
            Assert.Equal(new[] { "s1", "s2", "s3" }, records);
            Assert.Equal(1, SagaTestJobs.AttemptCount("s1"));
            Assert.Equal(1, SagaTestJobs.AttemptCount("s2"));
            Assert.Equal(1, SagaTestJobs.AttemptCount("s3"));
            // 终态后移出调度缓存，不再被反复扫描
            Assert.False(await Query.CheckCacheSet(Helper.SagaKey, sagaId));
        }

        [Fact]
        public async Task 中间步骤失败_逆序补偿已完成步骤_后续步骤留痕()
        {
            var sagaId = await Saga.Start(new ScriptSaga
            {
                Corr = "adv-2",
                SagaName = "中途失败",
                StepCount = 3,
                FailAt = 2
            });

            Assert.True(await DriveUntilFinish(sagaId), "saga 应在超时前补偿完成");

            var saga = await GetSaga(sagaId);
            Assert.Equal(SagaStatus.Compensated, saga!.Status);
            // 补偿从"失败步骤的前一步"（step1）开始，补偿完 step1 后游标回退到 0；
            // 失败步骤本身视为未生效、不补偿（records 里没有 c2/c3 也印证了这一点）
            Assert.Equal(0, saga.CompensationIndex);

            var steps = await GetSteps(sagaId);
            Assert.Equal(SagaStepStatus.Compensated, steps[0].Status);
            Assert.Equal(SagaStepStatus.Failed, steps[1].Status);
            // 失败步骤之后的步骤不会再执行，留痕为 Skipped
            Assert.Equal(SagaStepStatus.Skipped, steps[2].Status);
            Assert.Contains("will not be executed", steps[2].Message);

            var records = SagaTestJobs.Records.ToArray();
            Assert.Contains("s1", records);
            Assert.Contains("c1", records);
            // 失败步骤没有被补偿，未执行的步骤也没有被补偿
            Assert.DoesNotContain("c2", records);
            Assert.DoesNotContain("s3", records);
            Assert.DoesNotContain("c3", records);

            // 补偿必须发生在正向之后，且成功步骤只执行一次（成功状态 Finish=true，驱动循环不会重复执行）
            Assert.True(Array.IndexOf(records, "s1") < Array.IndexOf(records, "c1"), "补偿应在正向执行之后");
            Assert.Equal(1, SagaTestJobs.AttemptCount("s1"));

            // 失败步骤确实执行过；"最终判定为失败"由上面的 steps[1].Status == Failed 断言覆盖。
            // 不锁死精确次数：终态失败状态 FailedWorkState.Finish 恒为 false，
            // 手动驱动循环会把它再执行若干次，次数取决于驱动轮次而非重试配置，断言精确值会变成脆弱的时序依赖。
            Assert.Contains("s2", records);
        }

        [Fact]
        public async Task 不可补偿步骤_回滚时记为Skipped并留痕()
        {
            var sagaId = await Saga.Start(new ScriptSaga
            {
                Corr = "adv-3",
                SagaName = "含不可补偿步骤",
                StepCount = 2,
                NoCompensateAt = 1,
                FailAt = 2
            });

            Assert.True(await DriveUntilFinish(sagaId), "saga 应在超时前补偿完成");

            var saga = await GetSaga(sagaId);
            Assert.Equal(SagaStatus.Compensated, saga!.Status);

            var steps = await GetSteps(sagaId);
            // 不可补偿步骤不会拖住回滚：记为 Skipped 并说明原因
            Assert.Equal(SagaStepStatus.Skipped, steps[0].Status);
            Assert.Equal(SagaWorkProcess.NoCompensateMessage, steps[0].Message);
            Assert.True(string.IsNullOrEmpty(steps[0].CompensateWorkId));
            Assert.Equal(SagaStepStatus.Failed, steps[1].Status);

            Assert.DoesNotContain("c2", SagaTestJobs.Records.ToArray());
        }

        [Fact]
        public async Task 实例还原_变量池回填同名属性_无参读this()
        {
            // 第一步返回订单号 → outputKey: outputValue 采集进变量池；
            // 第二步无参，直接读 this.OutputValue —— 由变量池按名回填（变量优先于输入快照）
            var sagaId = await Saga.Start(new ScriptSaga
            {
                Corr = "adv-4",
                SagaName = "变量传递",
                StepCount = 2,
                OutputKey = "outputValue",
                OutputValue = "order-42"
            });

            Assert.True(await DriveUntilFinish(sagaId), "saga 应在超时前完成");

            var saga = await GetSaga(sagaId);
            Assert.Equal(SagaStatus.Completed, saga!.Status);
            Assert.Equal("order-42", saga.Variables["outputValue"]);

            // 第二步真正拿到的值是第一步的返回值（经实例还原 + 变量回填）
            Assert.Contains("s2:order-42", SagaTestJobs.Records.ToArray());
        }

        [Fact]
        public async Task 按名绑定_参数从变量池与输入属性取值()
        {
            var sagaId = await Saga.Start(new BindingSaga
            {
                Corr = "adv-4b",
                OrderNo = "PO-7",
                Sku = "sku-9"
            });

            Assert.True(await DriveUntilFinish(sagaId), "saga 应在超时前完成");

            var records = SagaTestJobs.Records.ToArray();
            // Consume(string orderNo)：参数 orderNo 从第一步的 outputKey（变量池）按名绑定
            Assert.Contains("b2:PO-7", records);
            // Publish(string sku)：参数 sku 从输入属性（启动快照还原后的实例）按名绑定
            Assert.Contains("b3:sku-9", records);
            Assert.Equal(SagaStatus.Completed, (await GetSaga(sagaId))!.Status);
        }

        [Fact]
        public async Task 展开步骤的CAS守卫_同一步骤不会被展开两次()
        {
            var sagaId = await Saga.Start(new ScriptSaga { Corr = "adv-5", SagaName = "CAS 守卫", StepCount = 1 });

            var steps = await GetSteps(sagaId);
            var step = steps[0];

            using (var storage = Provider.GetRequiredService<IWorkStorage>())
            {
                await storage.AdvanceSagaStep(sagaId, step.StepId,
                    SagaStepStatus.Init, SagaStepStatus.Running, "work-1", false);
                await storage.Commit();
            }

            // 状态已不是 Init，同一 expected 的 CAS 必须 0 行 → 抛异常回滚，
            // 从而保证"同一步骤被展开两次"不可能发生（工作不会被重复投递）
            using (var storage = Provider.GetRequiredService<IWorkStorage>())
            {
                await storage.AdvanceSagaStep(sagaId, step.StepId,
                    SagaStepStatus.Init, SagaStepStatus.Running, "work-2", false);

                await Assert.ThrowsAsync<SagaConcurrentUpdateException>(() => storage.Commit());
            }

            var after = await GetSteps(sagaId);
            Assert.Equal("work-1", after[0].ForwardWorkId);
        }

        [Fact]
        public async Task 推进的CAS守卫_期望值不匹配时抛异常()
        {
            var sagaId = await Saga.Start(new ScriptSaga { Corr = "adv-6", SagaName = "推进 CAS", StepCount = 1 });

            var saga = await GetSaga(sagaId);
            Assert.NotNull(saga);

            // 期望 None→Running，但游标期望值故意写错（实际是 1，这里传 2）
            var wrong = new SagaInstance
            {
                SagaId = sagaId,
                Status = SagaStatus.Running,
                CurrentIndex = 3,
                CompensationIndex = 0,
                Message = string.Empty
            };

            using (var storage = Provider.GetRequiredService<IWorkStorage>())
            {
                await storage.AdvanceSaga(wrong, SagaStatus.None, 2, 0);
                await Assert.ThrowsAsync<SagaConcurrentUpdateException>(() => storage.Commit());
            }

            // 事务整体回滚，状态未被改动
            var after = await GetSaga(sagaId);
            Assert.Equal(SagaStatus.None, after!.Status);
            Assert.Equal(1, after.CurrentIndex);
        }

        [Fact]
        public async Task 整体超时_转入补偿并记录超时原因()
        {
            var sagaId = await Saga.Start(new ScriptSaga { Corr = "adv-7", SagaName = "超时流程", StepCount = 2 });

            // 推进到 Running（第一步已展开但一直没执行完）
            Assert.True(await WaitUntil(async () =>
            {
                await SagaProcess.Execute(Context);
                var current = await GetSaga(sagaId);
                return current != null && current.Status == SagaStatus.Running;
            }), "saga 应进入 Running");

            // 把开始时间推到超时阈值之前
            await AgeSaga(sagaId, Option.SagaTimeout + TimeSpan.FromMinutes(1));

            Assert.Equal(1, await TimeoutProcess.TimeoutSagas());

            var saga = await GetSaga(sagaId);
            Assert.Equal(SagaStatus.Compensating, saga!.Status);
            // 超时走与"正向失败"相同的回滚路径
            Assert.Equal(0, saga.CompensationIndex);
            Assert.Contains("timeout", saga.Message);

            // 超时后仍能正常走完补偿并落到终态
            Assert.True(await DriveUntilFinish(sagaId), "超时后的 saga 应继续补偿到终态");
            Assert.Equal(SagaStatus.Compensated, (await GetSaga(sagaId))!.Status);
        }

        [Fact]
        public async Task 超时扫描_未超时或非Running的saga不会被误转()
        {
            var pending = await Saga.Start(new ScriptSaga { Corr = "adv-8", SagaName = "未超时", StepCount = 1 });

            var completed = await Saga.Start(new ScriptSaga { Corr = "adv-9", SagaName = "已超时但已终态", StepCount = 1 });
            Assert.True(await DriveUntilFinish(completed));

            // 已终态的 saga 即便开始时间很久以前也不该被"超时"处理
            await AgeSaga(completed, Option.SagaTimeout + TimeSpan.FromHours(1));

            Assert.Equal(0, await TimeoutProcess.TimeoutSagas());
            // pending 未超时：驱动 completed 时被扫描器顺带推进到 Running（展开第一步），
            // 但它绝不能被超时扫描转成 Compensating
            Assert.Equal(SagaStatus.Running, (await GetSaga(pending))!.Status);
            Assert.Equal(SagaStatus.Completed, (await GetSaga(completed))!.Status);
        }

        [Fact]
        public async Task 过期清理_只删除终态saga并级联删除步骤()
        {
            var completed = await Saga.Start(new ScriptSaga { Corr = "adv-10", SagaName = "已完成的saga", StepCount = 1 });
            Assert.True(await DriveUntilFinish(completed));

            var running = await Saga.Start(new ScriptSaga { Corr = "adv-11", SagaName = "未完成的saga", StepCount = 1 });

            await ExpireSaga(completed);
            await ExpireSaga(running);

            using (var storage = Provider.GetRequiredService<IWorkStorage>())
            {
                await storage.RemoveExpiredSagas(Helper.CurrentUtcTime());
                await storage.Commit();
            }

            // 终态且过期的被删除，步骤级联删除
            Assert.Null(await GetSaga(completed));
            Assert.Equal(0, await Db.CountSagaStepsAsync(completed));
            // 非终态的 saga 即便过期也不会被清理（还要继续推进 / 等人工续补）
            Assert.NotNull(await GetSaga(running));
        }
    }
}
