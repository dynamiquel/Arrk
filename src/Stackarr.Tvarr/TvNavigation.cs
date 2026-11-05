using MudBlazor;
using Stackarr.Ui;

namespace Stackarr.Tvarr;

/// <summary>
/// The TV module's navigation. Gated by <c>services:tv:enabled</c> so the entries disappear live
/// when the TV service is switched off, the shell never references the module to know this.
/// </summary>
public sealed class TvNavigation : INavigationProvider
{
    /// <summary>The config section every TV contribution is gated by.</summary>
    public const string EnabledConfigPath = "services:tv";

    public IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new(UiSections.Browse, "Movies", "/movies", Icons.Material.Filled.Movie, Order: 20, EnabledConfigPath: EnabledConfigPath),
        new(UiSections.Browse, "Shows", "/tv", Icons.Material.Filled.Tv, Order: 21, EnabledConfigPath: EnabledConfigPath)
    ];
}
