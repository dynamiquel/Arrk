using Arrk.Core.Models;
using Arrk.Notifications;

namespace Arrk.Core.Events;

/// <summary>
/// Unified, append-only event log. Any subsystem can record events and the UI can
/// query them by category/type/severity/text (including notification topics). Backed by SQLite
/// via EF Core.
/// </summary>
public interface IEventStore
{
    /// <summary>Record a single event.</summary>
    Task RecordAsync(SystemEventRecord record, CancellationToken ct = default);

    /// <summary>Record several events in one batch.</summary>
    Task RecordAsync(IEnumerable<SystemEventRecord> records, CancellationToken ct = default);

    /// <summary>
    /// Query events, newest first. All filters are optional; a null filter means "any".
    /// </summary>
    Task<IReadOnlyList<SystemEvent>> QueryAsync(SystemEventQuery query, CancellationToken ct = default);

    /// <summary>Delete events older than the given cutoff. Returns the number removed.</summary>
    Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default);

    /// <summary>Distinct event types, optionally scoped to a category.</summary>
    Task<IReadOnlyList<string>> GetEventTypesAsync(string? category = null, CancellationToken ct = default);
}

/// <summary>
/// Filters for <see cref="IEventStore.QueryAsync"/>. Null/empty members are ignored.
/// </summary>
public sealed class SystemEventQuery
{
    public string? Category { get; init; }
    public string? EventType { get; init; }
    public EventSeverity? MinSeverity { get; init; }

    /// <summary>Free text matched against the message, event type, source and notification topic.</summary>
    public string? Search { get; init; }
    public int Limit { get; init; } = 500;
    public DateTime? Since { get; init; }
}
