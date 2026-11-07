namespace Arrk.Notifications;

/// <summary>Shared interest model used by requests, watchlists, and the following UI.</summary>
public interface INotificationSubscriptionService
{
    Task<NotificationSubscription> SubscribeAsync(int userId, MediaEntityRef entity,
        NotificationSubscriptionOptions? options = null, string source = "manual", string? sourceId = null,
        CancellationToken ct = default);
    Task<bool> UpdateOptionsAsync(long subscriptionId, int userId, NotificationSubscriptionOptions options,
        CancellationToken ct = default);
    Task<bool> UnsubscribeAsync(int userId, string providerKey, string itemKey, CancellationToken ct = default);
    Task<int> RemoveSourceAsync(string source, string sourceId, CancellationToken ct = default);
    Task<bool> RemoveIfSourceAsync(int userId, string providerKey, string itemKey, string source,
        CancellationToken ct = default);
    Task<NotificationSubscription?> GetAsync(int userId, string providerKey, string itemKey, CancellationToken ct = default);
    Task<bool> IsSubscribedAsync(int userId, string providerKey, string itemKey, CancellationToken ct = default);
    Task<IReadOnlyList<NotificationSubscription>> GetForUserAsync(int userId, CancellationToken ct = default);
}
