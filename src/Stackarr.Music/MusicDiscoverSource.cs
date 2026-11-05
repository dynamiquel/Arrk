using MudBlazor;
using Stackarr.Music.Components.Pages;
using Stackarr.Ui;

namespace Stackarr.Music;

/// <summary>
/// Contributes the music section to the shell's Discover page. Gated by
/// <c>services:music:enabled</c>, so the tab appears and disappears with the service.
/// </summary>
public sealed class MusicDiscoverSource : IDiscoverSourceProvider
{
    public IReadOnlyList<DiscoverSource> Sources { get; } =
    [
        new("music", "Music", Icons.Material.Filled.LibraryMusic, typeof(MusicDiscover),
            Order: 40, EnabledConfigPath: MusicNavigation.EnabledConfigPath)
    ];
}
