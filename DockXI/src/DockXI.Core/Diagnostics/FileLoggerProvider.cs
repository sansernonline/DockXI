using Microsoft.Extensions.Logging;

namespace DockXI.Diagnostics;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logFilePath;
    private readonly object _writeLock = new();

    public FileLoggerProvider(string logsFolder)
    {
        Directory.CreateDirectory(logsFolder);
        _logFilePath = Path.Combine(
            logsFolder,
            $"DockXI-{DateTimeOffset.UtcNow:yyyyMMdd}.log");
    }

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(categoryName, _logFilePath, _writeLock);

    public void Dispose()
    {
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly string _path;
        private readonly object _gate;

        public FileLogger(string category, string path, object gate)
        {
            _category = category;
            _path = path;
            _gate = gate;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [{logLevel,-11}] {_category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += $"{Environment.NewLine}    {exception}";
            }

            lock (_gate)
            {
                try
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
                catch
                {
                    /* file logger swallows IO failures by design */
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
