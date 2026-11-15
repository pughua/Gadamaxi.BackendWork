using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Mongodb.Documents;
using Gadamaxi.BackendWork.Resources;
using Gadamaxi.BackendWork.Storages;
using MongoDB.Driver;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Mongodb
{
    /// <summary>
    /// Mongodb 版分布式锁：锁键即文档主键（唯一），过期后可被抢占，持锁期间由定时器续期。
    /// </summary>
    public class DistributedLock : IDistributedLock
    {
        private readonly MongoBackendWorkContext _context;

        public DistributedLock(MongoBackendWorkContext context)
        {
            Helper.CheckArgumentNull(context, nameof(context));

            _context = context;
        }

        public async Task<IDisposable> AcquireLock(string key, TimeSpan timeout)
        {
            var handle = new MongoLockHandle(_context, key, timeout);

            await handle.AcquireAsync();

            return handle;
        }
    }

    /// <summary>
    /// 一次持锁的句柄：获取成功即启动续期定时器，Dispose 时释放（且只释放属于自己的那把锁）。
    /// </summary>
    internal class MongoLockHandle : IDisposable
    {
        private readonly MongoBackendWorkContext _context;
        private readonly string _key;
        private readonly TimeSpan _timeout;
        private readonly string _owner;
        private Timer? _renewTimer;
        private bool _acquired;

        public MongoLockHandle(MongoBackendWorkContext context, string key, TimeSpan timeout)
        {
            Helper.CheckArgumentNull(context, nameof(context));
            Helper.CheckArgumentNull(key, nameof(key));

            _context = context;
            _key = key;
            _timeout = timeout;
            _owner = Helper.NewId();
        }

        public async Task AcquireAsync()
        {
            using (var cancelSource = new CancellationTokenSource(_timeout))
            {
                while (!_acquired && !cancelSource.IsCancellationRequested)
                {
                    try
                    {
                        var now = Helper.CurrentUtcTime();

                        // 1) 直接插入：主键唯一，插入成功即持锁
                        try
                        {
                            await _context.Locks.InsertOneAsync(new LockDocument
                            {
                                Key = _key,
                                Name = string.Empty,
                                Owner = _owner,
                                AcquiredUtcTime = now,
                                ExpireUtcTime = now.Add(_timeout)
                            });

                            _acquired = true;
                        }
                        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                        {
                            // 2) 已存在：仅当锁已过期才能抢占（filter 带 ExpireUtcTime < now，单文档更新原子）
                            var result = await _context.Locks.UpdateOneAsync(
                                Builders<LockDocument>.Filter.And(
                                    Builders<LockDocument>.Filter.Eq(t => t.Key, _key),
                                    Builders<LockDocument>.Filter.Lt(t => t.ExpireUtcTime, now)),
                                Builders<LockDocument>.Update
                                    .Set(t => t.AcquiredUtcTime, now)
                                    .Set(t => t.ExpireUtcTime, now.Add(_timeout))
                                    .Set(t => t.Owner, _owner));

                            _acquired = result.MatchedCount > Helper.Zero;
                        }
                    }
                    catch (Exception)
                    {
                        // 网络抖动等：继续重试直到超时
                    }

                    if (_acquired)
                    {
                        StartRenew();
                        return;
                    }

                    try
                    {
                        await Task.Delay(100, cancelSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            throw new LockBackendException(string.Format(Resource.AcquireLockError, _key));
        }

        /// <summary>
        /// 续期：持锁时间可能长于过期时间，按过期时间的一半周期性续期。
        /// 续期独立发起（不复用任何会话），与获取/释放路径互不干扰。
        /// </summary>
        private void StartRenew()
        {
            var interval = TimeSpan.FromMilliseconds(_timeout.TotalMilliseconds / 2);

            _renewTimer = new Timer(async _ =>
            {
                try
                {
                    var filter = Builders<LockDocument>.Filter.And(
                        Builders<LockDocument>.Filter.Eq(t => t.Key, _key),
                        Builders<LockDocument>.Filter.Eq(t => t.Owner, _owner));

                    await _context.Locks.UpdateOneAsync(filter,
                        Builders<LockDocument>.Update.Set(t => t.ExpireUtcTime, Helper.CurrentUtcTime().Add(_timeout)));
                }
                catch (Exception)
                {
                    // 续期失败不中断业务：真正兜底的是下一次抢占时的过期判定
                }
            }, null, interval, interval);
        }

        public void Dispose()
        {
            if (_renewTimer != null)
            {
                _renewTimer.Dispose();
                _renewTimer = null;
            }

            if (!_acquired)
            {
                return;
            }

            try
            {
                // 只释放属于自己的锁：锁过期被别人抢走后，这里的 Owner 已经不匹配，删除自然落空
                var filter = Builders<LockDocument>.Filter.And(
                    Builders<LockDocument>.Filter.Eq(t => t.Key, _key),
                    Builders<LockDocument>.Filter.Eq(t => t.Owner, _owner));

                _context.Locks.DeleteOne(filter);
            }
            catch (Exception)
            {
                // 释放失败交给过期抢占兜底
            }
        }
    }
}
