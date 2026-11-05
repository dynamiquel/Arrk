namespace Stackarr.Core.Models;

/// <summary>
/// Persisted system event shown in the unified event logs. Written through
/// <see cref="Stackarr.Core.Events.IEventStore"/> so any subsystem can contribute.
/// </summary>
public class SystemEvent
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }

    /// <summary>Broad category, e.g. "Download".</summary>
    public string Category { get; set; } = "";

    /// <summary>Specific event type, e.g. "TorrentAdded".</summary>
    public string EventType { get; set; } = "";

    /// <summary>
    /// Hierarchical notification topic this event maps to (e.g. <c>downloads.completed</c>), when the
    /// event carried a notification intent. Null for audit-only events. Lets the event feed treat a
    /// notification as a special kind of event and filter by topic.
    /// </summary>
    public string? Topic { get; set; }

    /// <summary>Severity name, e.g. "Info", "Warning", "Error".</summary>
    public string Severity { get; set; } = "";

    public string Message { get; set; } = "";

    /// <summary>Correlation key, e.g. torrent hash.</summary>
    public string? Source { get; set; }

    /// <summary>Human-readable source name, e.g. torrent name.</summary>
    public string? SourceName { get; set; }

    /// <summary>Optional structured payload (JSON).</summary>
    public string? Data { get; set; }
}
