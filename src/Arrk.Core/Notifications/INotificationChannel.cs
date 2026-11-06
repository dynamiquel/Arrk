namespace Arrk.Core.Notifications;

/// <summary>
/// Result of attempting to deliver a notification through a single channel.
/// </summary>
/// <param name="Success">False only when delivery was attempted and failed.</param>
/// <param name="Error">Failure detail when <paramref name="Success"/> is false.</param>
/// <param name="Skipped">True when the channel ignored the alert (e.g. an exclude filter).</param>
public sealed record NotificationDeliveryResult(bool Success, string? Error = null, bool Skipped = false)
{
    public static NotificationDeliveryResult Ok() => new(true);

    /// <summary>The channel filtered the alert out; nothing was sent and nothing failed.</summary>
    public static NotificationDeliveryResult Skip() => new(true, Skipped: true);

    public static NotificationDeliveryResult Fail(string error) => new(false, error);
}

/// <summary>
/// A delivery target for notifications (Discord, a generic webhook, and later email/Telegram/
/// Apprise). Channels are discovered through DI so new transports require no changes to the
/// dispatcher.
/// </summary>
public interface INotificationChannel
{
    /// <summary>Stable channel name recorded on the notification, e.g. <c>discord</c>.</summary>
    string Name { get; }

    /// <summary>True when the channel has enough configuration to attempt delivery.</summary>
    bool IsConfigured { get; }

    /// <summary>True when the channel can deliver to one account (a DM, email, push, ...).</summary>
    bool SupportsDirectDelivery => false;

    /// <summary>Delivers a single broadcast notification. Implementations should not throw for HTTP errors.</summary>
    Task<NotificationDeliveryResult> SendAsync(NotificationMessage message, CancellationToken ct = default);

    /// <summary>
    /// Delivers a notification to one account. Channels without direct delivery skip by default.
    /// The implementation is responsible for checking the user's opt-in and resolving their
    /// transport identity.
    /// </summary>
    Task<NotificationDeliveryResult> SendToUserAsync(
        int userId,
        NotificationMessage message,
        CancellationToken ct = default) => Task.FromResult(NotificationDeliveryResult.Skip());
}
