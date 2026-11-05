using Stackarr.Core.Events;

namespace Stackarr.Core.Notifications;

/// <summary>
/// A structured key/value row rendered inside a rich notification (Discord embed field,
/// generic webhook <c>fields</c> map, in-app detail rows).
/// </summary>
public sealed record NotificationField(string Name, string Value, bool Inline = true);

/// <summary>
/// A user-facing notification intent produced by a subsystem. Unlike a
/// <see cref="SystemEventRecord"/> (which is an audit entry), a notification is a curated,
/// formatted alert that may be delivered to external channels and the in-app inbox.
/// </summary>
public sealed record NotificationMessage
{
    /// <summary>Stable event key from <see cref="NotificationEvent"/>.</summary>
    public required string Key { get; init; }

    /// <summary>Short headline, e.g. "Download completed".</summary>
    public required string Title { get; init; }

    /// <summary>Human-readable detail line.</summary>
    public string Body { get; init; } = "";

    public EventSeverity Severity { get; init; } = EventSeverity.Info;

    /// <summary>Optional deep link (e.g. to the media item or the Stackarr UI).</summary>
    public string? Url { get; init; }

    /// <summary>Correlation key, e.g. a torrent hash or service key.</summary>
    public string? Source { get; init; }

    /// <summary>Human-readable source name, e.g. a torrent or movie title.</summary>
    public string? SourceName { get; init; }

    /// <summary>Optional structured payload (JSON) carried through to channels.</summary>
    public string? Data { get; init; }

    /// <summary>
    /// When set, the resulting inbox notification is addressed to this account only. Null uses
    /// <see cref="Audience"/>.
    /// </summary>
    public int? AudienceUserId { get; init; }

    /// <summary>Who receives the notification. Defaults to a broadcast.</summary>
    public NotificationAudience Audience { get; init; } = NotificationAudience.Broadcast;

    /// <summary>Explicit recipients when <see cref="Audience"/> is <see cref="NotificationAudience.Users"/>.</summary>
    public IReadOnlyList<int> AudienceUserIds { get; init; } = [];

    /// <summary>
    /// The media entity this event belongs to. Required for
    /// <see cref="NotificationAudience.Subscribers"/> and used to match per-user subscriptions.
    /// </summary>
    public MediaEntityRef? Entity { get; init; }

    /// <summary>Extra structured rows shown in rich channels.</summary>
    public IReadOnlyList<NotificationField> Fields { get; init; } = [];

    /// <summary>
    /// Optional presentation overrides. Channels merge these over the catalogue style for the
    /// topic, so a producer only needs to supply what is specific to this instance (e.g. a poster).
    /// </summary>
    public NotificationStyle? Style { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
