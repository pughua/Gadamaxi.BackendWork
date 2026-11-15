using Gadamaxi.BackendWork.Exceptions;
using LinqToDB;
using LinqToDB.Async;
using Xunit;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 复核 P0 修复的契约验证：Sql 版分布式锁补齐 Owner 作用域。
    /// <para>
    /// 修复前：抢占只按 <c>Key</c> 过滤（两个节点都能"更新 1 行成功"从而同时认为自己持锁）、
    /// 续期与释放不带 owner 限定（锁过期被别人抢走后，原持有者还能续别人的锁、删别人的锁）。
    /// 这里用"手工把锁行置为已过期 + 改变 Owner"的方式模拟"锁被别人抢走"，
    /// 断言原持有者既不能续期也不能释放。
    /// </para>
    /// <para>
    /// 锁行是 Sql 专属表（Mongo 版为 Locks 集合），因此本组用例仅在 Sql 后端执行。
    /// </para>
    /// </summary>
    public class DistributedLockTests : BackendWorkTestBase
    {
        private const string TestLockKey = "review-lock-owner-scope";

        /// <summary>
        /// 核心回归：锁被别的节点抢走（Owner 已变）后，原持有者 Dispose 不得删除该行。
        /// 修复前 Release 只按 Key 删除，会把别人的锁删掉 → 第三个节点立即可再次获取 → 互斥失效。
        /// </summary>
        [SqlBackendOnlyFact]
        public async Task 锁被他人抢占后_原持有者释放不删除他人锁()
        {
            using (await Lock.AcquireLock(TestLockKey, TimeSpan.FromMinutes(5)))
            {
                // 模拟"本节点的锁已过期、被别的节点抢占成功"：把 Owner 改成别人并保持未过期
                await SetOwner(TestLockKey, "another-node-owner");

                // 原持有者此刻退出：不能把别人的锁删掉
                await AssertOtherOwnerPreserved(TestLockKey);
            }
        }

        /// <summary>
        /// 续期也必须限定 owner：锁被别人抢走后，原持有者的心跳不能把别人的过期时间往后推，
        /// 否则锁会"永久有效"、抢占者之外谁都拿不到。
        /// </summary>
        [SqlBackendOnlyFact]
        public async Task 锁被他人抢占后_原持有者续期不生效()
        {
            // 过期时间给短一点，让心跳尽快触发一次（间隔 = timeout/2）
            using (await Lock.AcquireLock(TestLockKey, TimeSpan.FromSeconds(2)))
            {
                await SetOwner(TestLockKey, "another-node-owner");

                // 把过期时间压到"已过期"，再等心跳跑过两轮
                await SetExpire(TestLockKey, Helper.CurrentUtcTime().AddSeconds(-1));
                await Task.Delay(2500);

                var row = await ReadLockRow(TestLockKey);
                Assert.NotNull(row);
                // 心跳若未限定 owner，会把 ExpireUtcTime 推回 now+2s；限定后应保持我们写下的过期值
                Assert.True(row!.ExpireUtcTime < Helper.CurrentUtcTime(),
                    $"续期不应作用到他人的锁，实际过期时间 {row.ExpireUtcTime:O}");
            }
        }

        /// <summary>
        /// 正向路径不能回归：同一持有者持锁期间续期要生效，释放要真的删除行。
        /// </summary>
        [SqlBackendOnlyFact]
        public async Task 持锁期间续期生效_释放后被删除()
        {
            var handle = await Lock.AcquireLock(TestLockKey, TimeSpan.FromSeconds(2));
            try
            {
                var before = await ReadLockRow(TestLockKey);
                Assert.NotNull(before);

                // 心跳每 1s 一次（timeout/2），等两轮后过期时间应明显后移
                await Task.Delay(2500);
                var after = await ReadLockRow(TestLockKey);
                Assert.NotNull(after);
                Assert.True(after!.ExpireUtcTime > before!.ExpireUtcTime,
                    $"持锁期间应持续续期：before={before.ExpireUtcTime:O} after={after.ExpireUtcTime:O}");
            }
            finally
            {
                handle.Dispose();
            }

            Assert.Null(await ReadLockRow(TestLockKey));
        }

        /// <summary>
        /// 未拿到锁时不能释放；且超时抛出的必须是 <see cref="LockBackendException"/> 而不是
        /// <see cref="TaskCanceledException"/> —— 后者是修复前 `Task.Delay` 逃出 try 导致的，
        /// 调用方按 LockBackendException 捕获会漏。同时断言别人的锁没被删掉。
        /// </summary>
        [SqlBackendOnlyFact]
        public async Task 获取锁超时_抛约定的LockBackendException且不删除他人锁()
        {
            // 先让别的节点持有一把不过期的锁
            await SeedLockRow(TestLockKey, "another-node-owner", Helper.CurrentUtcTime().AddHours(1));

            // 本节点抢锁必然超时（锁未过期）。Assert.ThrowsAsync 要求异常类型精确匹配，
            // 因此这个断言同时锁住了"不能抛 TaskCanceledException"。
            await Assert.ThrowsAsync<LockBackendException>(
                () => Lock.AcquireLock(TestLockKey, TimeSpan.FromMilliseconds(300)));

            var row = await ReadLockRow(TestLockKey);
            Assert.NotNull(row);
            Assert.Equal("another-node-owner", row!.Owner);
        }

        // ---- 直查库的小工具（Sql 专属） ----

        private sealed record LockRow(string Key, string Owner, DateTime AcquiredUtcTime, DateTime ExpireUtcTime);

        private async Task<LockRow?> ReadLockRow(string key)
        {
            using var db = OpenDb();
            var row = await db.LockTable
                .Where(t => t.Key == key)
                .Select(t => new LockRow(t.Key, t.Owner, t.AcquiredUtcTime, t.ExpireUtcTime))
                .FirstOrDefaultAsync();
            return row;
        }

        private async Task SetOwner(string key, string owner)
        {
            using var db = OpenDb();
            await db.LockTable.Where(t => t.Key == key).Set(t => t.Owner, owner).UpdateAsync();
        }

        private async Task SetExpire(string key, DateTime expireUtcTime)
        {
            using var db = OpenDb();
            await db.LockTable.Where(t => t.Key == key).Set(t => t.ExpireUtcTime, expireUtcTime).UpdateAsync();
        }

        private async Task SeedLockRow(string key, string owner, DateTime expireUtcTime)
        {
            using var db = OpenDb();
            await db.LockTable
                .Value(t => t.Key, key)
                .Value(t => t.Name, string.Empty)
                .Value(t => t.Count, 0)
                .Value(t => t.Owner, owner)
                .Value(t => t.AcquiredUtcTime, Helper.CurrentUtcTime())
                .Value(t => t.ExpireUtcTime, expireUtcTime)
                .InsertAsync();
        }

        private async Task AssertOtherOwnerPreserved(string key)
        {
            var row = await ReadLockRow(key);
            Assert.NotNull(row);
            Assert.Equal("another-node-owner", row!.Owner);
        }
    }
}
