using Arrk.Core.Models;

namespace Arrk.Core.Notifications;

/// <summary>
/// The notification inbox and delivery service. It is the boundary between the event log
/// (audit) and user-facing alerts: it applies the configured policy, persists one inbox entry per
/// recipient, and delivers broadcasts through global channels or targeted alerts through each
/// recipient's direct channels.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Evaluates preferences and, when the event is enabled, records an inbox notification and
    /// delivers it to configured channels. Returns null when the event is disabled or unknown.
    /// </summary>
    Task<Notification?> PublishAsync(NotificationMessage message, CancellationToken ct = default);

    /// <summary>
    /// Publishes a notification addressed to a single account (in addition to external channels).
    /// Unlike <see cref="PublishAsync"/> broadcasts, only that account sees it in the in-app inbox.
    /// </summary>
    Task<Notification?> PublishToUserAsync(int userId, NotificationMessage message, CancellationToken ct = default);

    /// <summary>Queries inbox notifications, newest first.</summary>
    Task<IReadOnlyList<Notification>> QueryAsync(NotificationQuery query, CancellationToken ct = default);

    /// <summary>Marks a single notification as read. Returns the updated row, or null if missing.</summary>
    Task<Notification?> MarkReadAsync(long id, CancellationToken ct = default);

    /// <summary>Marks every unread notification as read. Returns the number updated.</summary>
    Task<int> MarkAllReadAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks unread notifications as read for one account (its own rows plus broadcasts).
    /// </summary>
    Task<int> MarkAllReadAsync(int? userId, CancellationToken ct = default);

    /// <summary>Count of unread notifications. When <paramref name="userId"/> is set, counts that
    /// account's rows plus broadcasts.</summary>
    Task<int> UnreadCountAsync(CancellationToken ct = default);

    /// <summary>Count of unread notifications visible to one account.</summary>
    Task<int> UnreadCountAsync(int? userId, CancellationToken ct = default);

    /// <summary>Deletes notifications older than the cutoff. Returns the number removed.</summary>
    Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}

/// <summary>Filters for <see cref="INotificationService.QueryAsync"/>. Null members are ignored.</summary>
public sealed class NotificationQuery
{
    public string? Category { get; init; }
    public string? Key { get; init; }
    public bool UnreadOnly { get; init; }
    public string? Search { get; init; }
    public DateTime? Since { get; init; }
    public int Limit { get; init; } = 200;

    /// <summary>When set, returns notifications addressed to this account plus broadcasts.</summary>
    public int? UserId { get; init; }

    /// <summary>When true (default) and <see cref="UserId"/> is set, broadcasts are included.</summary>
    public bool IncludeBroadcast { get; init; } = true;
}
