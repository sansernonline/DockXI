using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DockXI.Diagnostics;

/// <summary>
/// In-process circular buffer of recent log entries. Used by the in-app
/// Log Viewer so the user can inspect what went wrong without leaving the
/// app. Registered as a singleton; the InMemoryLogProvider writes here and
/// the UI reads here.
/// </summary>
public sealed class InMemoryLogStore
{
    private const int DefaultCapacity = 1000;

    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly int _capacity;
    private long _droppedCount;

    public InMemoryLogStore(int capacity = DefaultCapacity)
    {
        _capacity = capacity > 0 ? capacity : DefaultCapacity;
    }

    public event EventHandler<LogEntry>? EntryAdded;

    public long DroppedCount => System.Threading.Interlocked.Read(ref _droppedCount);

    public int Count => _entries.Count;

    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
            System.Threading.Interlocked.Increment(ref _droppedCount);
        }
        try { EntryAdded?.Invoke(this, entry); } catch { /* never throw to producer */ }
    }

    /// <summary>Snapshot the current entries in chronological order.</summary>
    public IReadOnlyList<LogEntry> Snapshot() => _entries.ToArray();

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
        System.Threading.Interlocked.Exchange(ref _droppedCount, 0);
    }
}

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? ExceptionDetail)
{
    public string FormattedLine
    {
        get
        {
            var line = $"{Timestamp.LocalDateTime:HH:mm:ss.fff} [{Level,-11}] {Category}: {Message}";
            if (!string.IsNullOrEmpty(ExceptionDetail))
            {
                line += Environment.NewLine + "    " + ExceptionDetail;
            }
            return line;
        }
    }
}

internal sealed class InMemoryLogProvider : ILoggerProvider
{
    private readonly InMemoryLogStore _store;

    public InMemoryLogProvider(InMemoryLogStore store)
    {
        _store = store;
    }

    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(categoryName, _store);

    public void Dispose() { }

    private sealed class InMemoryLogger : ILogger
    {
        private readonly string _category;
        private readonly InMemoryLogStore _store;

        public InMemoryLogger(string category, InMemoryLogStore store)
        {
            _category = category;
            _store = store;
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
            if (!IsEnabled(logLevel)) { return; }
            try
            {
                _store.Add(new LogEntry(
                    DateTimeOffset.Now,
                    logLevel,
                    _category,
                    formatter(state, exception),
                    exception?.ToString()));
            }
            catch
            {
                // Never throw from the log path.
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
