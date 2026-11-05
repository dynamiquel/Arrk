using System.Collections.Concurrent;
using Stackarr.Core.Events;
using Stackarr.Core.Models;
using Stackarr.Core.Notifications;

namespace Stackarr.TestSupport;

/// <summary>
/// In-memory <see cref="IEventStore"/> that records every event for assertions. Shared across
/// test projects so event-producing services can be tested uniformly. Also captures the
/// notification attached to each record so producers can be asserted on both concerns.
/// </summary>
public sealed class FakeEventStore : IEventStore
{
    readonly ConcurrentQueue<SystemEvent> _events = new();
    readonly ConcurrentQueue<SystemEventRecord> _records = new();
    long _nextId;

    public IReadOnlyList<SystemEvent> Events => _events.ToList();

    /// <summary>Raw records as written, including any attached notification.</summary>
    public IReadOnlyList<SystemEventRecord> Records => _records.ToList();

    /// <summary>Notifications attached to recorded events, in write order.</summary>
    public IReadOnlyList<NotificationMessage> Notifications =>
        _records.Where(r => r.Notification is not null).Select(r => r.Notification!).ToList();

    public Task RecordAsync(SystemEventRecord record, CancellationToken ct = default)
        => RecordAsync([record], ct);

    public Task RecordAsync(IEnumerable<SystemEventRecord> records, CancellationToken ct = default)
    {
        foreach (SystemEventRecord record in records)
        {
            _records.Enqueue(record);
            _events.Enqueue(new SystemEvent
            {
                Id = Interlocked.Increment(ref _nextId),
                Timestamp = record.Timestamp,
                Category = record.Category,
                EventType = record.EventType,
                Topic = record.Notification?.Key,
                Severity = record.Severity.ToString(),
                Message = record.Message,
                Source = record.Source,
                SourceName = record.SourceName,
                Data = record.Data
            });
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SystemEvent>> QueryAsync(SystemEventQuery query, CancellationToken ct = default)
    {
        IEnumerable<SystemEvent> q = _events.ToList();

        if (!string.IsNullOrWhiteSpace(query.Category))
            q = q.Where(e => e.Category == query.Category);
        if (!string.IsNullOrWhiteSpace(query.EventType))
            q = q.Where(e => e.EventType == query.EventType);
        if (query.MinSeverity is { } min)
            q = q.Where(e => Enum.TryParse(e.Severity, out EventSeverity s) && s >= min);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string search = query.Search;
            q = q.Where(e =>
                e.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (e.SourceName != null && e.SourceName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (e.Source != null && e.Source.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                e.EventType.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        if (query.Since is { } since)
            q = q.Where(e => e.Timestamp >= since);

        IReadOnlyList<SystemEvent> result = q
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Take(query.Limit <= 0 ? 500 : query.Limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        int removed = 0;
        List<SystemEvent> kept = [];
        while (_events.TryDequeue(out SystemEvent? e))
        {
            if (e.Timestamp < cutoff)
                removed++;
            else
                kept.Add(e);
        }
        foreach (SystemEvent e in kept)
            _events.Enqueue(e);
        return Task.FromResult(removed);
    }

    public Task<IReadOnlyList<string>> GetEventTypesAsync(string? category = null, CancellationToken ct = default)
    {
        IEnumerable<SystemEvent> q = _events;
        if (!string.IsNullOrWhiteSpace(category))
            q = q.Where(e => e.Category == category);
        IReadOnlyList<string> types = q.Select(e => e.EventType).Distinct().OrderBy(t => t).ToList();
        return Task.FromResult(types);
    }
}
