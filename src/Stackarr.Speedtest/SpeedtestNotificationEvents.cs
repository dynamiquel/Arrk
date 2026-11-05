using Stackarr.Core.Notifications;

namespace Stackarr.Speedtest;

/// <summary>Hierarchical notification topics for network speed tests (owned by the speedtest module).</summary>
public static class SpeedtestNotificationTopics
{
    public const string SpeedtestCompleted = "system.health.network.speedtest.completed";
    public const string SpeedtestDegraded = "system.health.network.speedtest.degraded";
    public const string SpeedtestRecovered = "system.health.network.speedtest.recovered";
    public const string VpnSpeedtestCompleted = "system.health.network.speedtest.vpn.completed";
    public const string VpnSpeedtestDegraded = "system.health.network.speedtest.vpn.degraded";
    public const string VpnSpeedtestRecovered = "system.health.network.speedtest.vpn.recovered";
    public const string VpnRestarted = "system.health.network.speedtest.vpn.restarted";
    public const string NetworkDegraded = "system.health.network.degraded";
}

/// <summary>Contributes network/speedtest notification events.</summary>
public sealed class SpeedtestNotificationEvents : INotificationEventProvider
{
    public IReadOnlyList<NotificationEventDefinition> Events { get; } =
    [
        new(SpeedtestNotificationTopics.SpeedtestCompleted, false, "A speed test completed (informational)", "🚀", 0x3498DB),
        new(SpeedtestNotificationTopics.SpeedtestDegraded, true, "A speed test fell below the configured floor", "🐢", 0xE67E22),
        new(SpeedtestNotificationTopics.SpeedtestRecovered, true, "A speed test recovered above the configured floor", "⚡", 0x2ECC71),
        new(SpeedtestNotificationTopics.VpnSpeedtestCompleted, false, "A VPN speed test completed (informational)", "🔐", 0x3498DB),
        new(SpeedtestNotificationTopics.VpnSpeedtestDegraded, true, "A VPN speed test fell below the configured floor", "🐢", 0xE67E22),
        new(SpeedtestNotificationTopics.VpnSpeedtestRecovered, true, "A VPN speed test recovered above the configured floor", "⚡", 0x2ECC71),
        new(SpeedtestNotificationTopics.VpnRestarted, true, "The VPN tunnel was restarted after a very low speed test", "🔄", 0xE67E22),
        new(SpeedtestNotificationTopics.NetworkDegraded, true, "Network throughput or latency degraded", "📉", 0xE67E22),
    ];

    public IReadOnlyList<NotificationGroupDefinition> Groups { get; } =
    [
        new("system", "System", "Stackarr and infrastructure events"),
        new("system.health", "Health", "Service, storage and network health"),
        new("system.health.network", "Network", "Network throughput and speed tests"),
        new("system.health.network.speedtest", "Speed tests", "Speed test results and degradation"),
    ];
}
