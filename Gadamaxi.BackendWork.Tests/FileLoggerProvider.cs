using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Gadamaxi.BackendWork.Tests
{
    /// <summary>
    /// 仅供排查用的文件日志 Provider：设置环境变量 BW_TEST_LOG_FILE 后生效，
    /// 把测试进程里的框架日志按行写入指定文件（Information 及以上）。
    /// 不设置环境变量时零开销、零行为，不影响常规测试运行。
    /// </summary>
    public class FileLoggerProvider : ILoggerProvider
    {
        private readonly object _lock = new object();
        private readonly StreamWriter _writer;

        public FileLoggerProvider(string path)
        {
            // FileShare.ReadWrite：同类的其它测试实例也会 append 同一个文件，不能独占
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, Encoding.UTF8)
            {
                AutoFlush = true
            };
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new FileLogger(this, categoryName);
        }

        public void Dispose()
        {
            _writer.Dispose();
        }

        internal void Write(string line)
        {
            lock (_lock)
            {
                _writer.WriteLine(line);
            }
        }

        private sealed class FileLogger : ILogger
        {
            private readonly FileLoggerProvider _provider;
            private readonly string _category;

            public FileLogger(FileLoggerProvider provider, string category)
            {
                _provider = provider;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                var line = $"{Helper.CurrentUtcTime():HH:mm:ss.fff} [{logLevel}] {_category}: {formatter(state, exception)}";
                if (exception != null)
                {
                    line += "\n" + exception;
                }

                _provider.Write(line);
            }
        }
    }
}
