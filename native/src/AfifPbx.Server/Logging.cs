using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AfifPbx.Server;

public sealed record LogLine(DateTime Time, string Level, string Category, string Message);

/// <summary>Writes logs to daily files in the data folder and keeps the last lines for the web panel.</summary>
public sealed class PbxLogProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly ConcurrentQueue<LogLine> _recent = new();
    private readonly object _fileLock = new();
    private const int Keep = 1000;

    public PbxLogProvider(string dataDir)
    {
        _dir = Path.Combine(dataDir, "Logs");
        Directory.CreateDirectory(_dir);
        // remove logs older than 30 days
        foreach (var f in Directory.GetFiles(_dir, "*.log"))
        {
            try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30)) File.Delete(f); } catch { }
        }
    }

    public IReadOnlyList<LogLine> Recent => _recent.ToArray();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(LogLevel level, string category, string message)
    {
        var line = new LogLine(DateTime.UtcNow, level.ToString(), category, message);
        _recent.Enqueue(line);
        while (_recent.Count > Keep) _recent.TryDequeue(out _);
        lock (_fileLock)
        {
            try
            {
                File.AppendAllText(Path.Combine(_dir, $"afif-pbx-{DateTime.Now:yyyyMMdd}.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} [{level}] {category}: {message}{Environment.NewLine}");
            }
            catch { }
        }
    }

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly PbxLogProvider _p;
        private readonly string _cat;
        public FileLogger(PbxLogProvider p, string cat)
        {
            _p = p;
            _cat = cat.StartsWith("Microsoft.") || cat.StartsWith("System.") ? cat.Split('.').Last() : cat;
        }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception is not null) msg += " — " + exception.Message;
            _p.Write(logLevel, _cat, msg);
        }
    }
}
