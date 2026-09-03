using System.Collections.Concurrent;

namespace Pvs.LineApp.Logging;

/// <summary>
/// A minimal, dependency-free file logger: appends one line per event to logs\pvs-yyyy-MM-dd.log (daily file).
/// The line PCs previously kept NO log file, so a silent failure (e.g. a DailyProductionCount write throwing)
/// left no trace to diagnose. This captures the app's own Information+ and any framework Warning+ — lean enough
/// to leave on permanently. Writes are lock-serialised and swallow their own IO errors so logging can never
/// break a request.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly LogLevel _min;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string dir, LogLevel min = LogLevel.Information)
    {
        _dir = dir; _min = min;
        try { Directory.CreateDirectory(_dir); } catch { /* logging must never throw */ }
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, c => new FileLogger(this, c));
    public void Dispose() => _loggers.Clear();

    private void Write(string category, LogLevel level, string message, Exception? ex)
    {
        if (level < _min) return;
        // Keep the file lean: app categories at Information+, but framework noise only at Warning+.
        bool framework = category.StartsWith("Microsoft.", StringComparison.Ordinal)
                         || category.StartsWith("System.", StringComparison.Ordinal);
        if (framework && level < LogLevel.Warning) return;
        string path = Path.Combine(_dir, $"pvs-{DateTime.Now:yyyy-MM-dd}.log");
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-11}] {category}: {message}"
                    + (ex is null ? "" : Environment.NewLine + ex) + Environment.NewLine;
        lock (_gate) { try { File.AppendAllText(path, line); } catch { /* ignore */ } }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _p;
        private readonly string _cat;
        public FileLogger(FileLoggerProvider p, string cat) { _p = p; _cat = cat; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level)) _p.Write(_cat, level, formatter(state, ex), ex);
        }
    }
}
