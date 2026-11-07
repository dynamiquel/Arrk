namespace Arrk.Core.Notifications;

/// <summary>Who a notification is for.</summary>
public enum NotificationAudience
{
    /// <summary>Everyone, delivered to global channels (the historical behaviour).</summary>
    Broadcast = 0,

    /// <summary>Users subscribed to the notification's <see cref="NotificationMessage.Entity"/>.</summary>
    Subscribers = 1,

    /// <summary>Exactly the accounts in <see cref="NotificationMessage.AudienceUserIds"/>.</summary>
    Users = 2,

    /// <summary>Global channels plus users subscribed to the notification's entity.</summary>
    BroadcastAndSubscribers = 3
}
