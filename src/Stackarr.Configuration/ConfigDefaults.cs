using Stackarr.Auth.Abstractions;
using Stackarr.Core.Configuration;

namespace Stackarr.Configuration;

public static class ConfigDefaults
{
    public static StackarrConfig GetDefaults() => new();

    /// <summary>
    /// Registers core-level config section providers into the registry.
    /// Module-level providers are registered by each module in ConfigureServices.
    /// </summary>
    public static void RegisterCoreSections(ConfigSectionRegistry registry)
    {
        registry.Register(new ConfigSectionProvider<MetadataConfig>("metadata", "Metadata",
            icon: "Movie", group: "Infrastructure", order: 5, groupOrder: 10));
        registry.Register(new ConfigSectionProvider<StorageConfig>("storage", "Storage",
            icon: "Storage", group: "Infrastructure", order: 10, groupOrder: 10));
        registry.Register(new ConfigSectionProvider<VpnConfig>("vpn", "VPN",
            icon: "VpnLock", group: "Infrastructure", order: 20, groupOrder: 10));
        registry.Register(new ConfigSectionProvider<RoutesConfig>("routes", "Routes",
            icon: "AltRoute", group: "Infrastructure", order: 30, groupOrder: 10));
        registry.Register(new ConfigSectionProvider<NotificationConfig>("notifications", "Notifications",
            icon: "Notifications", group: "Infrastructure", order: 40, groupOrder: 10));
        registry.Register(new ConfigSectionProvider<MonitoringConfig>("monitoring", "Monitoring",
            icon: "MonitorHeart", group: "Infrastructure", order: 50, groupOrder: 10));
        registry.Register(new ConfigSectionProvider<QualityProfilesConfig>("qualityProfiles", "Qualities",
            icon: "HighQuality", group: "Infrastructure", order: 60, groupOrder: 10));
        registry.Register(new ConfigSectionProvider<UiConfig>("ui", "Interface",
            icon: "Dashboard", group: "Application", order: 5, groupOrder: 0));
    }
}
