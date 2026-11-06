using MudBlazor;
using Arrk.Ui;

namespace Arrk.Configuration;

/// <summary>
/// The configuration module's navigation: the Configuration page (admin-only).
/// </summary>
public sealed class ConfigurationNavigation : INavigationProvider
{
    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.System, "Configuration", "/config", Icons.Material.Filled.Settings, Order: 11, AdminOnly: true)
    ];
}
