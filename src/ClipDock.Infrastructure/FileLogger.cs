using System.Text.Json;
using ClipDock.Core.Services;

namespace ClipDock.Infrastructure;

public sealed class FileLogger : IAppLogger
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileLogger(AppPaths paths)
    {
        paths.EnsureDirectories();
        _path = Path.Combine(paths.Logs, $"clipdock-{DateTime.UtcNow:yyyyMMdd}.log");
    }

    public void Information(string message, object? data = null) => Write("Information", message, null, data);
    public void Warning(string message, object? data = null) => Write("Warning", message, null, data);
    public void Error(string message, Exception? exception = null, object? data = null) => Write("Error", message, exception, data);

    private void Write(string level, string message, Exception? exception, object? data)
    {
        var record = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            level,
            message,
            exception = exception?.ToString(),
            data
        });
        lock (_gate)
        {
            File.AppendAllText(_path, record + Environment.NewLine);
        }
    }
}
