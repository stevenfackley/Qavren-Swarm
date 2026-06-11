using Microsoft.Extensions.Logging;

namespace QavrenSwarm.Services;

/// <summary>Minimal append-only file logger. Pairs with the stderr console logger so the
/// stdout JSON-RPC pipe is never touched, while a durable log survives the session.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FileLoggerProvider(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        // One held, append-mode writer (shared read so the log is tailable). AutoFlush keeps
        // it durable without the open/seek/close-per-line cost of File.AppendAllText.
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _gate);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly StreamWriter _writer;
        private readonly object _gate;

        public FileLogger(string category, StreamWriter writer, object gate)
        {
            _category = category;
            _writer = writer;
            _gate = gate;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var line = $"{DateTimeOffset.UtcNow:O} [{logLevel,-11}] {_category}: {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            lock (_gate)
            {
                _writer.WriteLine(line);
            }
        }
    }
}
