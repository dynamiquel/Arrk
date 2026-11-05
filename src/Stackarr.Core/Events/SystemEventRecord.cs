using System.ComponentModel;
using Stackarr.Core.Notifications;

namespace Stackarr.Core.Events;

/// <summary>
/// Severity of a system event. Drives the colour coding in the UI.
/// </summary>
public enum EventSeverity
{
    Trace = 0,
    Info = 1,
    Success = 2,
    Warning = 3,
    Error = 4
}

/// <summary>
/// Categories of system events. Values are stable strings so they can be persisted
/// and filtered without breaking when new categories are added.
/// </summary>
public static class EventCategory
{
    public const string Download = "Download";
    public const string Health = "Health";
    public const string Storage = "Storage";
    public const string Service = "Service";
    public const string Media = "Media";
    public const string System = "System";
    public const string Job = "Job";

    public static IReadOnlyList<string> All { get; } =
        [Download, Health, Storage, Service, Media, System, Job];
}

/// <summary>
/// A single event to be recorded in the unified event log.
/// This is the write model; the persisted entity is <see cref="Stackarr.Core.Models.SystemEvent"/>.
/// </summary>
public sealed record SystemEventRecord
{
    public required string Category { get; init; }
    public required string EventType { get; init; }
    public required string Message { get; init; }
    public EventSeverity Severity { get; init; } = EventSeverity.Info;

    /// <summary>Correlation key, e.g. a torrent hash or service key.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// Hierarchical notification topic for this event, when it belongs to the notification
    /// catalogue, independent of whether it also carries a <see cref="Notification"/>. Lets the
    /// event feed flag/filter audit events whose alerts are fanned out to specific recipients.
    /// </summary>
    public string? Topic { get; init; }

    /// <summary>Human-readable name of the source, e.g. a torrent or service name.</summary>
    public string? SourceName { get; init; }

    /// <summary>Optional structured payload (JSON).</summary>
    public string? Data { get; init; }

    /// <summary>
    /// Optional user-facing notification projected from this event. When null the event is
    /// audit-only. This is the single hook that connects the durable event log to the
    /// notification pipeline, so producers write one record and both concerns stay satisfied.
    /// </summary>
    public NotificationMessage? Notification { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
