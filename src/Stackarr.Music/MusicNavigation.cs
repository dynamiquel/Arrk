using MudBlazor;
using Stackarr.Ui;

namespace Stackarr.Music;

/// <summary>
/// The music module's navigation. Gated by <c>services:music:enabled</c> so the entry disappears
/// live when music is switched off.
/// </summary>
public sealed class MusicNavigation : INavigationProvider
{
    /// <summary>The config section every music contribution is gated by.</summary>
    public const string EnabledConfigPath = "services:music";

    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.Browse, "Music", "/music", Icons.Material.Filled.LibraryMusic, Order: 22, EnabledConfigPath: EnabledConfigPath)
    ];
}
