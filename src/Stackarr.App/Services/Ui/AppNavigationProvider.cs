using MudBlazor;
using Stackarr.Ui;

namespace Stackarr.App.Services.Ui;

/// <summary>
/// The shell's own navigation entries. Media entries (Movies, Shows, Music, ...) do not live here;
/// each module registers its own, so the shell never references a module to render the menu.
/// </summary>
public sealed class AppNavigationProvider : INavigationProvider
{
    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.Overview, "Dashboard", "/", Icons.Material.Filled.Dashboard, Order: 10, ExactMatch: true),

        new(UiSections.Browse, "Discover", "/discover", Icons.Material.Filled.Explore, Order: 10),

        new(UiSections.System, "Logs", "/logs", Icons.Material.Filled.Article, Order: 12, AdminOnly: true),

        new(UiSections.Info, "About", "/info", Icons.Material.Filled.Info, Order: 10)
    ];
}
