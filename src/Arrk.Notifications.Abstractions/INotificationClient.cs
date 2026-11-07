namespace Arrk.Notifications;

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
public interface INotificationClient
{
    /// <summary>Stable channel name recorded on the notification, e.g. <c>discord</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Deliver to one resolved audience, yielding an outcome as each endpoint completes. Yield Skip when disabled, filtered, unsupported,
    /// or when a user has not linked or enabled this transport. The client owns configuration,
    /// consent, external identities and protocol-specific retries; it never expands audiences.
    /// Multiple matching routes yield multiple outcomes, preserving partial success and cancellation.
    /// </summary>
    IAsyncEnumerable<NotificationDeliveryResult> SendAsync(
        NotificationMessage message,
        NotificationRecipient recipient,
        CancellationToken ct = default);
}
