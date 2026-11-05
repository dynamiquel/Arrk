namespace Stackarr.Core.Notifications;

/// <summary>
/// Contributes notification events and groups to the shared <see cref="NotificationEventRegistry"/>.
/// Each module implements this for the domain it owns, so event definitions live next to the code
/// that emits them rather than in the notifications module.
/// </summary>
public interface INotificationEventProvider
{
    /// <summary>The notifiable events this module owns.</summary>
    IReadOnlyList<NotificationEventDefinition> Events { get; }

    /// <summary>Optional curated groups this module contributes to the topic tree.</summary>
    IReadOnlyList<NotificationGroupDefinition> Groups => [];
}
