using MudBlazor;
using Arrk.Tvarr.Components.Pages;
using Arrk.Ui;

namespace Arrk.Tvarr;

/// <summary>
/// Contributes the TV discovery sections (Movies / Shows / Anime) to the shell's Discover page.
/// Each section is the same component with a different <c>Section</c> parameter, and all are gated
/// by <c>services:tv:enabled</c> so the tabs appear and disappear with the service.
/// </summary>
public sealed class TvDiscoverSource : IDiscoverSourceProvider
{
    public IReadOnlyList<DiscoverSource> Sources { get; } =
    [
        new("movies", "Movies", Icons.Material.Filled.Movie, typeof(TvDiscover), Order: 10,
            EnabledConfigPath: TvNavigation.EnabledConfigPath,
            Parameters: new Dictionary<string, object?> { ["Section"] = "movies" }),
        new("shows", "Shows", Icons.Material.Filled.Tv, typeof(TvDiscover), Order: 11,
            EnabledConfigPath: TvNavigation.EnabledConfigPath,
            Parameters: new Dictionary<string, object?> { ["Section"] = "shows" }),
        new("anime", "Anime", Icons.Material.Filled.AutoAwesome, typeof(TvDiscover), Order: 12,
            EnabledConfigPath: TvNavigation.EnabledConfigPath,
            Parameters: new Dictionary<string, object?> { ["Section"] = "anime" })
    ];
}
