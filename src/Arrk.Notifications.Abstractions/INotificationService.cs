namespace Arrk.Notifications;

/// <summary>
/// The notification inbox and delivery service. It is the boundary between the event log
/// (audit) and user-facing alerts: it applies the configured policy, persists one inbox entry per
/// recipient, and delivers to additive role and account destinations.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Evaluates preferences and, when the event is enabled, records an inbox notification and
    /// delivers it to registered clients. Returns the first inbox entry, or null when the topic
    /// is disabled/unknown or there are no eligible inbox accounts. Shared delivery can still
    /// occur when no local accounts exist.
    /// </summary>
    Task<Notification?> PublishAsync(NotificationMessage message, CancellationToken ct = default);

    /// <summary>
    /// Replaces all audience selectors with one account, using only its inbox and direct clients.
    /// Never sends to role endpoints, even when the supplied message selects them.
    /// </summary>
    Task<Notification?> PublishToUserAsync(int userId, NotificationMessage message, CancellationToken ct = default);

    /// <summary>Queries inbox notifications, newest first.</summary>
    Task<IReadOnlyList<Notification>> QueryAsync(NotificationQuery query, CancellationToken ct = default);

    /// <summary>Marks an entry owned by the given account as read, or returns null.</summary>
    Task<Notification?> MarkReadAsync(long id, int userId, CancellationToken ct = default);

    /// <summary>Marks only the given account's entries as read.</summary>
    Task<int> MarkAllReadAsync(int userId, CancellationToken ct = default);

    /// <summary>Count of unread notifications visible to one account.</summary>
    Task<int> UnreadCountAsync(int userId, CancellationToken ct = default);

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

    /// <summary>When set, returns only this account's inbox entries.</summary>
    public int? UserId { get; init; }
}
