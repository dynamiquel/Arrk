namespace Stackarr.Core.Notifications;

/// <summary>
/// Per-account opt-in and delivery state for one notification transport. The channel name is a
/// stable transport key (<c>discord</c>, later <c>telegram</c>, <c>email</c>, ...); destination
/// values are transport-specific.
/// </summary>
public class UserNotificationChannel
{
    public long Id { get; set; }

    public int UserId { get; set; }

    public string Channel { get; set; } = "";

    /// <summary>Whether the user has explicitly opted in to this transport.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Transport-specific destination, e.g. a cached Discord DM channel id. The external identity
    /// itself is still owned by the auth module; this is only a delivery cache.
    /// </summary>
    public string? Destination { get; set; }

    public DateTime? VerifiedAt { get; set; }
    public DateTime? LastSentAt { get; set; }
    public string? LastError { get; set; }
    public DateTime UpdatedAt { get; set; }
}
