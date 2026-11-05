using MudBlazor;
using Stackarr.Ui;

namespace Stackarr.DownloadManagement;

/// <summary>
/// The download-management module's navigation. The unified Downloads page is always available (torrent
/// management is core), so the entry is not config-gated.
/// </summary>
public sealed class DownloadManagementNavigation : INavigationProvider
{
    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.Browse, "Downloads", "/downloads", Icons.Material.Filled.CloudDownload, Order: 41)
    ];
}
