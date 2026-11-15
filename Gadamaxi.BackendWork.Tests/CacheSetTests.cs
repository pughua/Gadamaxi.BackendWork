using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Sql;
using Gadamaxi.BackendWork.Sql.Tables;
using Gadamaxi.BackendWork.Storages;
using Gadamaxi.BackendWork.Works.States;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 调度缓存集合（<c>CacheSet</c> 表，由 <see cref="IWorkStorage.AddCacheSet"/> 写入）的契约测试。
    /// <para>
    /// 它的定位是「按 Type 分桶、按 Score 排序的到期索引」，而不是统计表：调度进程每轮用
    /// <c>GetCacheSet(key, 0, 当前时间戳, 上限)</c> 取出"已到期"的条目来驱动批量 / 周期 / 延迟 / saga 的推进。
    /// Score 的语义由登记方决定（延迟任务=到期时刻，批量=登记时刻，周期=下次执行时刻）。
    /// </para>
    /// </summary>
    public class CacheSetTests : BackendWorkTestBase
    {
        private const string Key = "cacheset-test";

        private IWorkStorage NewStorage() => Provider.GetRequiredService<IWorkStorage>();

        /// <summary>取"当前时刻之前到期"的条目，与调度进程每轮的口径一致</summary>
        private Task<List<string>> Due(string key, long now)
            => Query.GetCacheSet(key, Helper.Zero, now, int.MaxValue);

        private Task<List<TestCacheSetRow>> Rows() => Db.GetCacheSetRowsAsync();

        [Fact]
        public async Task AddCacheSet_是延迟事务_提交前对外不可见()
        {
            using (var storage = NewStorage())
            {
                await storage.AddCacheSet(Key, 1, "v1");

                // 写入只是入队，Commit 之前没有任何东西落库（延迟事务的基本约定）
                Assert.False(await Query.CheckCacheSet(Key, "v1"));
                Assert.Empty(await Rows());

                await storage.Commit();
            }

            Assert.True(await Query.CheckCacheSet(Key, "v1"));
            Assert.Single(await Rows());
        }

        [Fact]
        public async Task AddCacheSet_同一Type可登记多个Value_按Score区间取到期项()
        {
            var now = Helper.CurrentUtcTime().UnixMillisecondStamp();

            using (var storage = NewStorage())
            {
                await storage.AddCacheSet(Key, now - 10000, "已到期");
                await storage.AddCacheSet(Key, now, "刚好到期");
                await storage.AddCacheSet(Key, now + 10000, "未到期");
                await storage.Commit();
            }

            // 调度进程每轮只取"已到期"的：Score 严格小于当前时刻
            Assert.Equal(new[] { "已到期" }, (await Due(Key, now)).ToArray());

            // 终点放到无限远才能读到该 Type 下登记的全部值
            var all = await Query.GetCacheSet(Key, Helper.Zero, long.MaxValue, int.MaxValue);
            Assert.Equal(new[] { "刚好到期", "已到期", "未到期" },
                all.OrderBy(t => t, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public async Task GetCacheSet_区间左闭右开()
        {
            using (var storage = NewStorage())
            {
                await storage.AddCacheSet(Key, 1000, "at-start");
                await storage.AddCacheSet(Key, 2000, "at-end");
                await storage.Commit();
            }

            // 起点命中、终点排除：Score >= start && Score < end
            var between = await Query.GetCacheSet(Key, 1000, 2000, int.MaxValue);
            Assert.Equal(new[] { "at-start" }, between.ToArray());
        }

        [Fact]
        public async Task AddCacheSet_不同Type互相隔离_删除只命中指定Type()
        {
            using (var storage = NewStorage())
            {
                await storage.AddCacheSet("type-a", 1, "shared");
                await storage.AddCacheSet("type-b", 1, "shared");
                await storage.AddCacheSet("type-a", 1, "only-a");
                await storage.Commit();
            }

            Assert.Equal(3, (await Rows()).Count);
            Assert.Equal(new[] { "only-a", "shared" },
                (await Query.GetCacheSet("type-a", Helper.Zero, long.MaxValue, int.MaxValue))
                    .OrderBy(t => t, StringComparer.Ordinal).ToArray());

            using (var storage = NewStorage())
            {
                await storage.RemoveCacheSet("type-a", "shared");
                await storage.Commit();
            }

            Assert.False(await Query.CheckCacheSet("type-a", "shared"));
            // 同一个 Value 在别的 Type 下仍然存在：删除按 (Type,Value) 精确定位
            Assert.True(await Query.CheckCacheSet("type-b", "shared"));
            Assert.True(await Query.CheckCacheSet("type-a", "only-a"));
        }

        [Fact]
        public async Task UpdateCacheSet_推进Score_已扫到的条目不会立刻被再次扫到()
        {
            var now = Helper.CurrentUtcTime().UnixMillisecondStamp();
            var span = TimeSpan.FromMinutes(1);

            using (var storage = NewStorage())
            {
                await storage.AddCacheSet(Key, now, "w1");
                await storage.Commit();
            }

            // 第一轮：已到期，被扫出
            Assert.Equal(new[] { "w1" }, (await Due(Key, now + 1)).ToArray());

            // 模拟 WorkFactory.GetCacheDatas：扫出后立刻把 Score 推到"下一轮"，
            // 这是"轮询扫描不会把同一条目重复处理"的唯一保障
            using (var storage = NewStorage())
            {
                await storage.UpdateCacheSet(Key, now + (long)span.TotalMilliseconds, "w1");
                await storage.Commit();
            }

            Assert.Empty(await Due(Key, now + 1));

            // 推后的时刻到达后重新可见
            Assert.Equal(new[] { "w1" }, (await Due(Key, now + (long)span.TotalMilliseconds + 1)).ToArray());
        }

        [Fact]
        public async Task UpdateCacheSet_只改指定Type下的同名Value()
        {
            using (var storage = NewStorage())
            {
                await storage.AddCacheSet("type-a", 10, "shared");
                await storage.AddCacheSet("type-b", 10, "shared");
                await storage.Commit();
            }

            using (var storage = NewStorage())
            {
                await storage.UpdateCacheSet("type-a", 9999, "shared");
                await storage.Commit();
            }

            Assert.Empty(await Query.GetCacheSet("type-a", Helper.Zero, 11, int.MaxValue));
            Assert.Equal(new[] { "shared" },
                (await Query.GetCacheSet("type-b", Helper.Zero, 11, int.MaxValue)).ToArray());
        }

        [Fact]
        public async Task AddCacheSet_重复登记同一Type和Value_会撞唯一约束并回滚整事务()
        {
            using (var storage = NewStorage())
            {
                await storage.AddCacheSet(Key, 1, "dup");
                await storage.AddCacheSet("other", 1, "first");
                await storage.Commit();
            }

            using (var storage = NewStorage())
            {
                await storage.AddCacheSet(Key, 2, "dup");       // 同一 (Type,Value) 重复登记
                await storage.AddCacheSet("other", 2, "second");

                // (Type,Value) 上有唯一约束 → 插入失败，整个事务回滚
                await Assert.ThrowsAnyAsync<Exception>(() => storage.Commit());
            }

            // 回滚是整事务的：同一批里的 "second" 也没落库
            Assert.False(await Query.CheckCacheSet("other", "second"));
            Assert.True(await Query.CheckCacheSet("other", "first"));

            // 已存在的行既没被删，Score 也没被第二次写入覆盖
            var row = (await Rows()).Single(t => t.Type == Key && t.Value == "dup");
            Assert.Equal(1d, row.Score);

            // 这也是 SagaFactory.Wake / 批量重试都采取"先 Remove 再 Add"的原因
        }

        [Fact]
        public async Task AddCacheSet_不写过期时间_也没有按过期时间的定期清理()
        {
            // 清理进程末尾会按该间隔等待一轮，调成 1ms 让用例不必真等一小时
            Option.CleanupPollingDelay = TimeSpan.FromMilliseconds(1);

            using (var storage = NewStorage())
            {
                await storage.AddCacheSet(Key, 1, "orphan");
                await storage.Commit();
            }

            // AddCacheSet 只写 Type/Score/Value，ExpireUtcTime 这一列从不写入
            var orphan = (await Rows()).Single(t => t.Value == "orphan");
            Assert.Null(orphan.ExpireUtcTime);

            // 造一个"已过期的终态工作"，用来证明这一轮清理确实跑了（否则下面的断言是空过）
            await Db.SeedWorkAsync("expired-work", SucceededWorkState.Name, Helper.CurrentUtcTime().AddDays(-1));

            await new ExpiredDataCleanupProcess(CreateLogger<ExpiredDataCleanupProcess>())
                .Execute(NewProcessContext());

            // 终态工作确实被清掉了
            Assert.False(await WorkRowExists("expired-work"));

            // 但调度缓存的行还在：CacheSet 没有基于过期时间的回收，
            // 只能靠各生命周期处理器显式 RemoveCacheSet，漏掉一条就是永久残留
            Assert.True(await Query.CheckCacheSet(Key, "orphan"));
        }
    }
}
