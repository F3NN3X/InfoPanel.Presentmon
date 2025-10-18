using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace PresentMon.BridgeService.Logging;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly LogLevel _minimumLevel;

    public FileLoggerProvider(string filePath, LogLevel minimumLevel)
    {
        _filePath = filePath;
        _minimumLevel = minimumLevel;

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_filePath, _lock, categoryName, _minimumLevel);

    public void Dispose()
    {
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _filePath;
        private readonly object _lock;
        private readonly string _categoryName;
        private readonly LogLevel _minimumLevel;

        public FileLogger(string filePath, object syncLock, string categoryName, LogLevel minimumLevel)
        {
            _filePath = filePath;
            _lock = syncLock;
            _categoryName = categoryName;
            _minimumLevel = minimumLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
            {
                return;
            }

            var timestamp = DateTimeOffset.Now.ToString("O");
            var message = formatter(state, exception);
            var logLine = $"{timestamp} [{logLevel}] {_categoryName}: {message}";

            if (exception != null)
            {
                logLine = $"{logLine}{Environment.NewLine}{exception}";
            }

            try
            {
                lock (_lock)
                {
                    File.AppendAllText(_filePath, logLine + Environment.NewLine);
                }
            }
            catch
            {
                // Swallow logging errors to avoid crashing the service.
            }
        }
    }
}
