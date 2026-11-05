using MudBlazor;
using Stackarr.Ui;

namespace Stackarr.Configuration;

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
