using Gadamaxi.BackendWork.Exceptions;
using Gadamaxi.BackendWork.Resources;
using Gadamaxi.BackendWork.Server;
using Gadamaxi.BackendWork.Sql.Tables;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gadamaxi.BackendWork.Sql
{
    public class DistributedLockProvider : IDisposable
    {
        private readonly DBConnection _db;
        private readonly DataOptions _option;
        private readonly LockTable _data;
        private readonly TimeSpan _timeOutSpan;
        private bool _lockSuccess;
        private readonly ILogger<DistributedLockProvider> _logger;
        private Exception? _lastEx;
        private Timer? _heartTimer;
        private string _id;
        public DistributedLockProvider(DataOptions option, ILogger<DistributedLockProvider> logger,string lockKey,TimeSpan expirtime,string name="")
        {
            Helper.CheckArgumentNull(option, nameof(option));
            Helper.CheckArgumentNull(lockKey, nameof(lockKey));
            Helper.CheckArgumentNull(expirtime, nameof(expirtime));
            _id = Helper.NewId();
            _timeOutSpan = expirtime;
            _logger = logger;
            _option = option;
            _data = new LockTable
            {
                Key = lockKey,
                Name = name,
                AcquiredUtcTime = Helper.CurrentUtcTime(),
                ExpireUtcTime = Helper.CurrentUtcTime().Add(expirtime),
                Owner = _id
            };
            _db = new DBConnection(option);
            _lockSuccess = false;
        }

        public async Task AcquireLock()
        {
            using (var cancelSource = new CancellationTokenSource(_timeOutSpan))
            {
                await InnerAcquireLock(cancelSource.Token);
            };

            if (!_lockSuccess)
            {
                if(_lastEx!=null)
                {
                    throw new LockBackendException(string.Format(Resource.AcquireLockError, _data.Key),_lastEx);
                }
                else
                {
                    throw new LockBackendException(string.Format(Resource.AcquireLockError, _data.Key));
                }
            }

            //_logger.LogTrace($"AcquireLock {_data.Key} success! {_id}");
        }

        private async Task InnerAcquireLock(CancellationToken ct)
        {

            while (!_lockSuccess && !ct.IsCancellationRequested)
            {
                try
                {
                    var now = Helper.CurrentUtcTime();
                    var q = from t in _db.LockTable
                            where t.Key == _data.Key
                            select t;
                    var data = await q.FirstOrDefaultAsync();
                    if (data == null)
                    {
                        try
                        {
                            await _db.InsertAsync(_data);
                            _lockSuccess = true;
                        }
                        catch
                        {
                            // 主键已存在说明并发插入被别的节点抢先，交给下一轮抢占判定，不算致命错误。
                            _lastEx = null;
                        }
                    }
                    else if (data.ExpireUtcTime < now)
                    {
                        // 抢占必须是 CAS：只在"仍然过期"时才写成功。只按 Key 过滤会让两个节点
                        // 同时更新 1 行、同时认为自己拿到了锁（Mongo 版用 Owner + ExpireUtcTime
                        // 过滤，此处与之对齐）。
                        var updatecount = await _db.LockTable
                            .Where(s => s.Key == _data.Key && s.ExpireUtcTime < now)
                            .Set(s => s.AcquiredUtcTime, now)
                            .Set(s => s.ExpireUtcTime, now.Add(_timeOutSpan))
                            .Set(s => s.Owner, _id)
                            .UpdateAsync();
                        if (updatecount > 0)
                        {
                            _lockSuccess = true;
                        }

                    }

                    if (_lockSuccess)
                    {
                        await StartRefresh();
                        return;
                    }

                }
                catch (Exception ex)
                {
                    _lastEx = ex;
                }
                if (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(100, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // 等待期间恰好超时：这是"抢不到锁"的正常出口，不能让
                        // TaskCanceledException 逃出循环——否则 AcquireLock 抛的是
                        // TaskCanceledException 而不是约定的 LockBackendException，
                        // 调用方按 LockBackendException 捕获会漏掉（Mongo 版此处已同款处理）。
                        break;
                    }
                }
            }
        }

        private Task StartRefresh()
        {
            TimeSpan timerInterval = TimeSpan.FromMilliseconds(_timeOutSpan.TotalMilliseconds / 2);
            _heartTimer = new Timer(async _ =>
            {
                try
                {
                    // 续期使用独立连接，避免与获取/释放路径共用非线程安全的 _db（#5）。
                    // 每行一个短连接：获取/释放路径不会与续期回调并发访问同一 DataConnection。
                    using (var refreshDb = new DBConnection(_option))
                    {
                        // 显式事务：linq2db 在未有活动事务时调用 CommitTransaction 会抛异常，
                        // 此处与 WorkStorage.Commit 保持一致，确保续期在所有 Provider 下都真正落库。
                        using (var trans = refreshDb.BeginTransaction())
                        {
                            await refreshDb.LockTable
                                .Where(t => t.Key == _data.Key && t.Owner == _id)
                                .Set(t => t.ExpireUtcTime, Helper.CurrentUtcTime().Add(_timeOutSpan))
                                .UpdateAsync();
                            await trans.CommitAsync();
                        }
                    }
                }
                catch(Exception ex)
                {
                    _logger.LogWarning(Resource.UpdateLockError, _data.Key);
                    _logger.LogInformation($"update lock error {ex.Message}-{ex.GetStacks()}");
                }
            },null, timerInterval, timerInterval);

            return Task.CompletedTask;
        }

        private void Release()
        {
            // 只释放属于自己的锁：锁过期被别的节点抢走后，原持有者不能把别人的锁删掉。
            _db.LockTable.Where(t => t.Key == _data.Key && t.Owner == _id).Delete();
        }

        public void Dispose()
        {
            if (_heartTimer != null)
            {
                _heartTimer.Dispose();
            }

            // 没拿到锁就什么都不释放（Mongo 版同样先判 _acquired）。
            if (_lockSuccess)
            {
                //_logger.LogTrace($"AcquireLock {_data.Key} release! {_id}");
                Release();
            }

            _db.Dispose();
        }
    }
}
