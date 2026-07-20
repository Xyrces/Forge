using Microsoft.Extensions.Logging;

namespace Forge.Core;

/// <summary>
/// Minimal file logger provider so the Windows Service hosted
/// Forge.Core has a visible log path. Default Microsoft.Extensions.Logging
/// has no built-in file sink; we use a thin custom provider that
/// appends to a file with size-based rotation.
/// </summary>
public sealed class ForgeFileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly long _maxBytes;
    private long _currentSize;

    public ForgeFileLoggerProvider(string filePath, long maxBytes = 5_000_000)
    {
        _filePath = filePath;
        _maxBytes = maxBytes;
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(filePath))
            {
                _currentSize = new FileInfo(filePath).Length;
            }
        }
        catch { /* best-effort */ }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(string line)
    {
        try
        {
            lock (_lock)
            {
                if (_currentSize > _maxBytes)
                {
                    // simple rotate: rename to .1 and start fresh
                    var rotated = _filePath + ".1";
                    if (File.Exists(rotated)) File.Delete(rotated);
                    File.Move(_filePath, rotated);
                    _currentSize = 0;
                }
                File.AppendAllText(_filePath, line + Environment.NewLine);
                _currentSize += line.Length + Environment.NewLine.Length;
            }
        }
        catch { /* best-effort */ }
    }

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly ForgeFileLoggerProvider _provider;
        private readonly string _category;
        public FileLogger(ForgeFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {_category}: {formatter(state, exception)}";
            if (exception is not null) line += $" | {exception.GetType().Name}: {exception.Message}";
            _provider.Write(line);
        }
    }
}
