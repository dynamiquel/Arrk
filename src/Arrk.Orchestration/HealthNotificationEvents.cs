using Arrk.Core.Notifications;

namespace Arrk.Orchestration;

/// <summary>Hierarchical notification topics for service health (owned by the orchestration module).</summary>
public static class HealthNotificationTopics
{
    public const string ServiceDown = "system.health.service.down";
    public const string ServiceRecovered = "system.health.service.recovered";
}

/// <summary>Contributes service-health notification events.</summary>
public sealed class HealthNotificationEvents : INotificationEventProvider
{
    public IReadOnlyList<NotificationEventDefinition> Events { get; } =
    [
        new(HealthNotificationTopics.ServiceDown, true, "A service container stopped or became unhealthy", "🛑", 0xE74C3C),
        new(HealthNotificationTopics.ServiceRecovered, true, "A service came back up after being down", "✅", 0x2ECC71),
    ];

    public IReadOnlyList<NotificationGroupDefinition> Groups { get; } =
    [
        new("system", "System", "Arrk and infrastructure events"),
        new("system.health", "Health", "Service, storage and network health"),
        new("system.health.service", "Service health", "Containers going down or recovering"),
    ];
}
