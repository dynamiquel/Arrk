using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Stackarr.App.Services;

public sealed class LogEntry
{
    public DateTime Timestamp { get; init; }
    public string Level { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Exception { get; init; }
    public string? SourceContext { get; init; }

    public string FormattedMessage => $"[{Timestamp:HH:mm:ss.fff}] [{Level,-12}] {Message}" + (Exception != null ? $"\n{Exception}" : "");
}

public sealed class LogCaptureService : ILogEventSink, IDisposable
{
    private readonly ConcurrentQueue<LogEntry> _buffer = new();
    private const int MaxEntries = 5000;
    private bool _disposed;

    public LogCaptureService()
    {
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed)
            return;

        try
        {
            var entry = new LogEntry
            {
                Timestamp = logEvent.Timestamp.DateTime,
                Level = logEvent.Level.ToString().ToUpperInvariant(),
                Message = logEvent.RenderMessage(),
                Exception = logEvent.Exception?.ToString(),
                SourceContext = logEvent.Properties.TryGetValue("SourceContext", out var src) ? src.ToString() : null
            };

            _buffer.Enqueue(entry);

            while (_buffer.Count > MaxEntries && _buffer.TryDequeue(out _))
            {
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LogCaptureService] Failed to capture log event: {ex}");
        }
    }

    public IReadOnlyList<LogEntry> GetLogs(int count, string? filter = null)
    {
        var entries = _buffer.ToArray();
        var result = entries.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var lowerFilter = filter.ToLowerInvariant();
            result = result.Where(e =>
                e.Message.Contains(lowerFilter, StringComparison.OrdinalIgnoreCase) ||
                (e.Exception?.Contains(lowerFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                e.Level.Contains(lowerFilter, StringComparison.OrdinalIgnoreCase) ||
                (e.SourceContext?.Contains(lowerFilter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return result.Reverse().Take(count).Reverse().ToList();
    }

    public void Clear()
    {
        while (_buffer.TryDequeue(out _))
        {
        }
    }

    public int Count => _buffer.Count;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Clear();
    }
}