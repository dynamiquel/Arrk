using System.ComponentModel;

namespace Arrk.Core.Configuration;

/// <summary>
/// Presentation settings shared by the shell. The default dashboard is a structured list of
/// sections and tiles, so an administrator can change what a new account starts with without
/// touching code.
/// </summary>
public class UiConfig
{
    public const string SectionName = "ui";

    [Description("Dashboard sections and tiles applied to accounts that have not customised their dashboard.")]
    [DisplayName("Default dashboard")]
    public List<DashboardSectionConfig> DefaultDashboardSections { get; set; } = UiDefaults.CreateDefaultSections();
}

/// <summary>One section in the default dashboard.</summary>
public class DashboardSectionConfig
{
    [Description("Heading shown above the section.")]
    public string Title { get; set; } = "";

    public List<DashboardTileConfig> Tiles { get; set; } = [];
}

/// <summary>One tile in a default dashboard section.</summary>
public class DashboardTileConfig
{
    [Description("Stable tile id, for example system.host.")]
    public string Id { get; set; } = "";

    public DashboardTileSize Size { get; set; } = DashboardTileSize.Medium;
}

/// <summary>The size names accepted by the dashboard config.</summary>
public enum DashboardTileSize
{
    Small,
    Medium,
    Large,
    VeryLarge
}

/// <summary>The built-in values used when the ui config section is absent.</summary>
public static class UiDefaults
{
    public static List<DashboardSectionConfig> CreateDefaultSections() =>
    [
        new()
        {
            Title = "Host",
            Tiles =
            [
                new() { Id = "services.status", Size = DashboardTileSize.VeryLarge },
                new() { Id = "system.updated", Size = DashboardTileSize.Small },
                new() { Id = "services.unhealthy", Size = DashboardTileSize.Small },
                new() { Id = "system.host", Size = DashboardTileSize.Small },
                new() { Id = "system.storage", Size = DashboardTileSize.Medium },
                new() { Id = "system.speedtest", Size = DashboardTileSize.Medium }
            ]
        },
        new()
        {
            Title = "Library",
            Tiles =
            [
                new() { Id = "requests.pending", Size = DashboardTileSize.VeryLarge },
                new() { Id = "tv.continue-watching", Size = DashboardTileSize.VeryLarge },
                new() { Id = "tv.watchlists", Size = DashboardTileSize.VeryLarge },
                new() { Id = "music.recently-played", Size = DashboardTileSize.Large },
                new() { Id = "music.playlists", Size = DashboardTileSize.Large }
            ]
        }
    ];
}
