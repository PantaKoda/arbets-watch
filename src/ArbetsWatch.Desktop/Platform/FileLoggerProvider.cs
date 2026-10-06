using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ArbetsWatch.Desktop.Platform;

/// <summary>
/// A bounded plain-text log: <c>arbetswatch.log</c> rolls over to <c>arbetswatch.1.log</c> at 1 MB, so at most
/// two files (about 2 MB) are kept. Messages contain summaries only, never API responses.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 1024 * 1024;
    private readonly string _path;
    private readonly string _previous;
    private readonly Lock _lock = new();

    public FileLoggerProvider(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "arbetswatch.log");
        _previous = Path.Combine(directory, "arbetswatch.1.log");
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                {
                    File.Move(_path, _previous, overwrite: true);
                }

                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var shortCategory = category[(category.LastIndexOf('.') + 1)..];
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {logLevel,-11} {shortCategory}: {formatter(state, exception)}");
            if (exception is not null)
            {
                line += $" | {exception.GetType().Name}: {exception.Message}";
            }

            provider.Write(line + Environment.NewLine);
        }
    }
}
