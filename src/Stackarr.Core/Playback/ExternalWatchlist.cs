
namespace Stackarr.Core.Playback;

/// <summary>One item shown in an external (service-backed) watchlist.</summary>
public sealed record ExternalWatchlistItem(
    string Key,
    string Title,
    string? Subtitle,
    string? PosterUrl,
    int? Year,
    MediaDomain Domain,
    int TmdbId,
    int TvdbId,
    string? Href = null,
    bool IsInLibrary = false,
    string? Overview = null,
    IReadOnlyList<string>? Genres = null,
    double? Rating = null,
    int? RuntimeMinutes = null,
    string? OriginalTitle = null,
    string? SeasonLabel = null);

/// <summary>
/// A watchlist whose source of truth lives in another service (MyAnimeList today). The module that
/// owns the service implements this; the TV UI merges these lists into the normal watchlist shelf
/// and picker without storing duplicate rows in the watchlist tables.
/// </summary>
public interface IExternalWatchlist
{
    string Key { get; }

    string Name { get; }

    string Icon { get; }

    /// <summary>Optional image/mask URL used instead of <see cref="Icon"/> in the tab strip.</summary>
    string? IconUrl => null;

    int Order { get; }

    /// <summary>Optional "view all" destination for the shelf.</summary>
    string? ViewAllHref => null;

    /// <summary>Raised after the external list changes so the shelf/picker can refresh.</summary>
    event Action? Changed;

    Task<bool> IsAvailableAsync(int userId, CancellationToken ct = default);

    Task<IReadOnlyList<ExternalWatchlistItem>> GetItemsAsync(
        int userId, MediaDomain domain, CancellationToken ct = default);

    /// <summary>Whether this list can represent the given local movie/series at all.</summary>
    Task<bool> CanHandleAsync(int userId, WatchlistItemRef item, CancellationToken ct = default);

    Task<bool> ContainsAsync(int userId, WatchlistItemRef item, CancellationToken ct = default);

    Task AddAsync(int userId, WatchlistItemRef item, CancellationToken ct = default);

    Task RemoveAsync(int userId, WatchlistItemRef item, CancellationToken ct = default);
}
