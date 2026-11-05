
namespace Stackarr.Core.Playback;

/// <summary>
/// Identity of a media item for playback-state lookups. Keyed off external provider ids so a
/// player integration never depends on Stackarr's internal library ids.
/// </summary>
public sealed record MediaKey(
    MediaDomain Domain = default,
    int TmdbId = 0,
    int TvdbId = 0,
    int SeasonNumber = 0,
    int EpisodeNumber = 0)
{
    public bool IsEpisode => SeasonNumber > 0 || EpisodeNumber > 0;

    public string CacheKey => $"{Domain}:{TmdbId}:{TvdbId}:{SeasonNumber}:{EpisodeNumber}";
}

/// <summary>Playback state of a single item for a single user.</summary>
public sealed record WatchState(
    bool IsPlayed,
    double PlayedPercentage,
    long PositionTicks,
    DateTime? LastPlayed,
    int PlayCount)
{
    public static WatchState Unwatched { get; } = new(false, 0, 0, null, 0);

    public bool IsInProgress => !IsPlayed && PositionTicks > 0;

    /// <summary>Position as a fraction 0..1 of the total runtime, when known.</summary>
    public double PositionFraction => PlayedPercentage <= 0 ? 0 : Math.Clamp(PlayedPercentage / 100.0, 0, 1);
}

/// <summary>
/// Aggregated watch state for a whole series: how many episodes have been watched against the
/// total, and the furthest/last episode watched (so a card can show
/// "last episode watched / total episodes").
/// </summary>
public sealed record SeriesWatchState(
    int WatchedEpisodes,
    int TotalEpisodes,
    int? LastWatchedSeason,
    int? LastWatchedEpisode)
{
    public static SeriesWatchState Empty { get; } = new(0, 0, null, null);

    public bool IsFullyWatched => TotalEpisodes > 0 && WatchedEpisodes >= TotalEpisodes;

    public double Progress => TotalEpisodes <= 0 ? 0 : Math.Clamp((double)WatchedEpisodes / TotalEpisodes, 0, 1);
}

/// <summary>A resumable item for the Continue Watching shelf.</summary>
public sealed record ContinueWatchingItem(
    MediaKey Key,
    string Title,
    string? ImageUrl,
    string? FallbackImageUrl,
    string? LogoUrl,
    string? PlayUrl,
    string? ParentTitle,
    double Progress,
    long PositionTicks,
    long RuntimeTicks,
    DateTime? LastPlayed,
    int SeriesTmdbId = 0,
    int SeriesTvdbId = 0,
    bool IsUpNext = false)
{
    public bool IsEpisode => Key.IsEpisode;

    public long RemainingTicks => Math.Max(0, RuntimeTicks - PositionTicks);

    public string ScopeLabel => IsEpisode ? $"S{Key.SeasonNumber}E{Key.EpisodeNumber}" : "";

    /// <summary>Artwork is resolved by the app (TMDB → arr); the player only supplies identity + progress.</summary>
    public ContinueWatchingItem WithArt(string? imageUrl, string? fallbackImageUrl, string? logoUrl) =>
        this with { ImageUrl = imageUrl, FallbackImageUrl = fallbackImageUrl, LogoUrl = logoUrl };
}

/// <summary>A single entry in a user's watch history.</summary>
public sealed record WatchHistoryEntry(
    string Username,
    MediaKey Key,
    string Title,
    DateTime WatchedAt,
    double Progress,
    bool Completed);

/// <summary>
/// A media player that can report what users have watched. Contributed by a player module
/// (Jellyfin today; Navidrome or another player later). The UI consumes only this contract, so the
/// player can be replaced without touching Stackarr's own code.
/// </summary>
public interface IWatchStateProvider
{
    /// <summary>Player name, e.g. <c>Jellyfin</c>.</summary>
    string Name { get; }

    /// <summary>Whether the player is configured and reachable enough to answer queries.</summary>
    bool IsAvailable { get; }

    /// <summary>Watch state for a batch of items (movies/episodes). Missing keys are simply absent.</summary>
    Task<IReadOnlyDictionary<MediaKey, WatchState>> GetStatesAsync(
        string username,
        IReadOnlyCollection<MediaKey> keys,
        CancellationToken ct = default);

    /// <summary>Aggregated watch state for a batch of series, keyed by TMDb or TVDB id.</summary>
    Task<IReadOnlyDictionary<int, SeriesWatchState>> GetSeriesStatesAsync(
        string username,
        IReadOnlyCollection<int> tmdbOrTvdbIds,
        CancellationToken ct = default);

    /// <summary>Items the user has started but not finished.</summary>
    Task<IReadOnlyList<ContinueWatchingItem>> GetContinueWatchingAsync(
        string username,
        int limit = 20,
        CancellationToken ct = default);

    /// <summary>Recent watch history for a user.</summary>
    Task<IReadOnlyList<WatchHistoryEntry>> GetHistoryAsync(
        string username,
        int limit = 100,
        CancellationToken ct = default);
}

/// <summary>
/// Facade over the contributed <see cref="IWatchStateProvider"/>s. Uses the first available player
/// and degrades to empty results when none is configured, so the UI never needs to know which
/// player is in use (or whether one exists at all).
/// </summary>
public sealed class WatchStateService(IEnumerable<IWatchStateProvider> providers)
{
    readonly IReadOnlyList<IWatchStateProvider> _providers = providers.ToList();

    /// <summary>The player currently answering watch-state queries, if any.</summary>
    public IWatchStateProvider? Active => _providers.FirstOrDefault(p => p.IsAvailable);

    public bool IsAvailable => Active is not null;

    public string? ProviderName => Active?.Name;

    public Task<IReadOnlyDictionary<MediaKey, WatchState>> GetStatesAsync(
        string username, IReadOnlyCollection<MediaKey> keys, CancellationToken ct = default) =>
        Active is { } provider
            ? provider.GetStatesAsync(username, keys, ct)
            : Task.FromResult<IReadOnlyDictionary<MediaKey, WatchState>>(new Dictionary<MediaKey, WatchState>());

    public Task<IReadOnlyDictionary<int, SeriesWatchState>> GetSeriesStatesAsync(
        string username, IReadOnlyCollection<int> tmdbOrTvdbIds, CancellationToken ct = default) =>
        Active is { } provider
            ? provider.GetSeriesStatesAsync(username, tmdbOrTvdbIds, ct)
            : Task.FromResult<IReadOnlyDictionary<int, SeriesWatchState>>(new Dictionary<int, SeriesWatchState>());

    public Task<IReadOnlyList<ContinueWatchingItem>> GetContinueWatchingAsync(
        string username, int limit = 20, CancellationToken ct = default) =>
        Active is { } provider
            ? provider.GetContinueWatchingAsync(username, limit, ct)
            : Task.FromResult<IReadOnlyList<ContinueWatchingItem>>([]);

    public Task<IReadOnlyList<WatchHistoryEntry>> GetHistoryAsync(
        string username, int limit = 100, CancellationToken ct = default) =>
        Active is { } provider
            ? provider.GetHistoryAsync(username, limit, ct)
            : Task.FromResult<IReadOnlyList<WatchHistoryEntry>>([]);
}
