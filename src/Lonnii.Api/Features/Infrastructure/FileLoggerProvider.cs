namespace Lonnii.Api.Features.Infrastructure;

/// <summary>
/// Writes the log to <c>{data}\logs\api-yyyy-MM-dd.log</c>, keeping 14 days.
///
/// <para>
/// On a shop's host the API is started at boot by a scheduled task with no console, so without
/// this every failure (an import that rolls back, a sync that stops) leaves no trace anyone can
/// read. The levels and filters from appsettings apply as for the console.
/// </para>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int KeepDays = 14;

    private readonly string _directory;
    private readonly object _gate = new();
    private string? _currentPath;
    private StreamWriter? _writer;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);

        foreach (var old in Directory.EnumerateFiles(directory, "api-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-KeepDays)) File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var path = Path.Combine(_directory, $"api-{DateTime.Now:yyyy-MM-dd}.log");
                if (path != _currentPath)
                {
                    _writer?.Dispose();
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        AutoFlush = true,
                    };
                    _currentPath = path;
                }

                _writer!.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A full disk, or a log file another account owns, must not take the API down with it.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) _writer?.Dispose();
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;

            provider.Write(line);
        }
    }
}
