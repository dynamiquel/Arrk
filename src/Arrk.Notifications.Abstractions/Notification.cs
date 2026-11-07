namespace Arrk.Notifications;

/// <summary>
/// A persisted, user-facing notification shown in the in-app inbox and recorded alongside the
/// outcome of external delivery (Discord/webhook). This is distinct from
/// a system event: the event store is the complete audit trail of everything that
/// happened, whereas a <see cref="Notification"/> is the opted-in, delivered subset the user
/// actually wants to see.
/// </summary>
public class Notification
{
    public long Id { get; set; }

    /// <summary>Stable dotted topic from a module's event catalogue.</summary>
    public string Key { get; set; } = "";

    /// <summary>Category key, e.g. <c>downloads</c>.</summary>
    public string Category { get; set; } = "";

    public string Title { get; set; } = "";
    public string Body { get; set; } = "";

    /// <summary>Severity name, e.g. "Info", "Warning", "Error".</summary>
    public string Severity { get; set; } = "Info";

    public string? Url { get; set; }
    public string? Source { get; set; }
    public string? SourceName { get; set; }

    /// <summary>
    /// The account that owns this inbox entry and its independent read state. New entries always
    /// have an owner; legacy ownerless rows are not included in any user's inbox.
    /// </summary>
    public int? UserId { get; set; }

    /// <summary>Optional structured payload (JSON) as produced by the source subsystem.</summary>
    public string? Data { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When the user read the notification in the app; null while unread.</summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>True when at least one external channel accepted the notification.</summary>
    public bool Delivered { get; set; }

    /// <summary>Comma-separated channel names that accepted the notification.</summary>
    public string? Channels { get; set; }

    /// <summary>Delivery failures, retained even when another client or destination succeeded.</summary>
    public string? Error { get; set; }
}
