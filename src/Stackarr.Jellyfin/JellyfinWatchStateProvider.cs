using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core.Enums;
using Stackarr.Core.Playback;

namespace Stackarr.Jellyfin;

/// <summary>
/// Watch-state provider backed by Jellyfin. Resolves the Stackarr account to a Jellyfin user and
/// reads per-item <c>UserData</c>. The rest of Stackarr depends only on
/// <see cref="IWatchStateProvider"/>, so another player can replace Jellyfin without touching the UI.
/// </summary>
public sealed class JellyfinWatchStateProvider(
    JellyfinClient client,
    JellyfinLinkService links,
    IOptionsMonitor<JellyfinConfig> config,
    ILogger<JellyfinWatchStateProvider> logger) : IWatchStateProvider
{
    readonly SemaphoreSlim _lock = new(1, 1);
    readonly Dictionary<string, string?> _userIds = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, (DateTime BuiltAt, WatchIndex Index)> _indexes = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, (DateTime BuiltAt, List<JellyfinItem> Episodes)> _episodeCache = [];
    static readonly TimeSpan IndexLifetime = TimeSpan.FromSeconds(30);
    static readonly TimeSpan EpisodeLifetime = TimeSpan.FromSeconds(30);

    public string Name => "Jellyfin";

    public bool IsAvailable => config.CurrentValue.Enabled;

    sealed class WatchIndex
    {
        public Dictionary<int, WatchState> MoviesByTmdb { get; } = [];
        public Dictionary<(int Tvdb, int Season, int Episode), WatchState> EpisodesByKey { get; } = [];
        public Dictionary<int, SeriesWatchState> SeriesByTvdb { get; } = [];
        public Dictionary<int, SeriesWatchState> SeriesByTmdb { get; } = [];
        public Dictionary<string, (int Tmdb, int Tvdb)> SeriesById { get; } = [];
        public Dictionary<string, (DateTime PlayedAt, int Season, int Episode)> LastPlayedEpisodeBySeriesId { get; } = [];
    }

    public async Task<IReadOnlyDictionary<MediaKey, WatchState>> GetStatesAsync(
        string username,
        IReadOnlyCollection<MediaKey> keys,
        CancellationToken ct = default)
    {
        Dictionary<MediaKey, WatchState> result = [];
        if (!IsAvailable || keys.Count == 0)
            return result;

        WatchIndex? index = await GetIndexAsync(username, ct);
        if (index is null)
            return result;

        foreach (MediaKey key in keys)
        {
            if (key.IsEpisode)
            {
                if (key.TvdbId > 0 &&
                    index.EpisodesByKey.TryGetValue((key.TvdbId, key.SeasonNumber, key.EpisodeNumber), out WatchState? epState))
                {
                    result[key] = epState;
                }
            }
            else if (key.TmdbId > 0 && index.MoviesByTmdb.TryGetValue(key.TmdbId, out WatchState? movieState))
            {
                result[key] = movieState;
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<int, SeriesWatchState>> GetSeriesStatesAsync(
        string username,
        IReadOnlyCollection<int> tmdbOrTvdbIds,
        CancellationToken ct = default)
    {
        Dictionary<int, SeriesWatchState> result = [];
        if (!IsAvailable || tmdbOrTvdbIds.Count == 0)
            return result;

        WatchIndex? index = await GetIndexAsync(username, ct);
        if (index is null)
            return result;

        foreach (int id in tmdbOrTvdbIds)
        {
            if (index.SeriesByTvdb.TryGetValue(id, out SeriesWatchState? byTvdb))
                result[id] = byTvdb;
            else if (index.SeriesByTmdb.TryGetValue(id, out SeriesWatchState? byTmdb))
                result[id] = byTmdb;
        }

        return result;
    }

    public async Task<IReadOnlyList<ContinueWatchingItem>> GetContinueWatchingAsync(
        string username,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            return [];

        string? userId = await ResolveUserIdAsync(username, ct);
        if (userId is null)
            return [];

        try
        {
            string playBase = links.IsAvailable ? links.BaseUrl : "";
            WatchIndex? index = await GetIndexAsync(username, ct);

            // A series can contribute several in-progress episodes. Fetch a wider window so
            // collapsing them into one card never starves the rest of the shelf.
            JellyfinItemsResult resume = await client.GetResumeItemsAsync(userId, Math.Max(limit * 3, 30), ct);
            JellyfinItemsResult nextUp = await client.GetNextUpAsync(userId, Math.Max(limit * 2, 20), ct);

            List<(ContinueWatchingItem Item, int Order)> selected = [];
            Dictionary<string, List<(JellyfinItem Source, ContinueWatchingItem Item)>> bySeries = [];
            Dictionary<string, int> orderBySeries = [];
            int order = 0;

            foreach (JellyfinItem item in resume.Items)
            {
                if (item.UserData is null || item.UserData.Played)
                    continue;

                ContinueWatchingItem mapped = MapItem(item, index, playBase);

                if (!mapped.IsEpisode)
                {
                    // A film inside the tail window is finished; do not offer it again.
                    if (!ContinueWatchingRules.IsEffectivelyFinished(
                        mapped.PositionTicks, mapped.RuntimeTicks, isEpisode: false))
                    {
                        selected.Add((mapped, order++));
                    }
                    continue;
                }

                string seriesKey = ContinueWatchingRules.SeriesKey(mapped);
                if (!bySeries.TryGetValue(seriesKey, out List<(JellyfinItem Source, ContinueWatchingItem Item)>? group))
                {
                    group = [];
                    bySeries[seriesKey] = group;
                    orderBySeries[seriesKey] = order++;
                }

                group.Add((item, mapped));
            }

            Dictionary<string, (JellyfinItem Source, ContinueWatchingItem Item)> upNextBySeries = [];
            foreach (JellyfinItem item in nextUp.Items)
            {
                ContinueWatchingItem mapped = MapItem(item, index, playBase, upNext: true);
                DateTime? lastPlayed = item.SeriesId is { Length: > 0 } seriesId &&
                    index is not null &&
                    index.LastPlayedEpisodeBySeriesId.TryGetValue(
                        seriesId, out (DateTime PlayedAt, int Season, int Episode) played)
                            ? played.PlayedAt
                            : null;

                upNextBySeries[ContinueWatchingRules.SeriesKey(mapped)] =
                    (item, mapped with { LastPlayed = lastPlayed });
            }

            // One card per series: the episode the viewer is actually on. A later played episode
            // means an older half-finished one is stale, so NextUp wins; otherwise the newest
            // in-progress episode stays, or turns into the following episode when it is finished.
            foreach ((string seriesKey, List<(JellyfinItem Source, ContinueWatchingItem Item)> group) in bySeries)
            {
                (JellyfinItem Source, ContinueWatchingItem Item) latest =
                    ContinueWatchingRules.SelectLatest(group, entry => entry.Item);

                (DateTime PlayedAt, int Season, int Episode)? lastPlayedEpisode =
                    latest.Source.SeriesId is { Length: > 0 } seriesId &&
                    index is not null &&
                    index.LastPlayedEpisodeBySeriesId.TryGetValue(
                        seriesId, out (DateTime PlayedAt, int Season, int Episode) played)
                            ? played
                            : null;

                if (upNextBySeries.TryGetValue(seriesKey, out (JellyfinItem Source, ContinueWatchingItem Item) movedPast) &&
                    HasMovedPast(latest.Item, lastPlayedEpisode))
                {
                    selected.Add((movedPast.Item, orderBySeries[seriesKey]));
                    upNextBySeries.Remove(seriesKey);
                    continue;
                }

                ContinueWatchingItem? chosen = ContinueWatchingRules.IsEffectivelyFinished(
                    latest.Item.PositionTicks,
                    latest.Item.RuntimeTicks,
                    isEpisode: true)
                        ? await ResolveNextEpisodeAsync(latest.Source, index, playBase, latest.Item.LastPlayed, ct)
                        : latest.Item;

                if (chosen is not null)
                    selected.Add((chosen, orderBySeries[seriesKey]));

                upNextBySeries.Remove(seriesKey);
            }

            // Series with no in-progress episode still deserve a card when an earlier episode was
            // finished: Jellyfin's NextUp knows the next unwatched episode.
            foreach ((JellyfinItem Source, ContinueWatchingItem Item) upNextItem in upNextBySeries.Values)
                selected.Add((upNextItem.Item, order++));

            return selected
                .OrderByDescending(entry => entry.Item.LastPlayed ?? DateTime.MinValue)
                .ThenBy(entry => entry.Order)
                .Select(entry => entry.Item)
                .Take(limit)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Jellyfin resume lookup failed for {User}", username);
            return [];
        }
    }

    // Continue Watching helpers

    /// <summary>
    /// Maps a Jellyfin item to the player-agnostic shelf model. Artwork stays <c>null</c> here:
    /// the app resolves it from TMDB/arr, never from Jellyfin.
    /// </summary>
    ContinueWatchingItem MapItem(
        JellyfinItem item,
        WatchIndex? index,
        string playBase,
        bool upNext = false,
        string? seriesIdOverride = null)
    {
        bool isEpisode = string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase);
        JellyfinUserItemData? data = item.UserData;
        double progress = !upNext && data is { PlayedPercentage: > 0 }
            ? Math.Clamp(data.PlayedPercentage / 100.0, 0, 1)
            : 0;

        string? seriesId = seriesIdOverride ?? item.SeriesId;
        int seriesTmdb = 0;
        int seriesTvdb = 0;
        if (isEpisode && !string.IsNullOrEmpty(seriesId) &&
            index is not null && index.SeriesById.TryGetValue(seriesId, out (int Tmdb, int Tvdb) ids))
        {
            seriesTmdb = ids.Tmdb;
            seriesTvdb = ids.Tvdb;
        }

        return new ContinueWatchingItem(
            ToKey(item),
            item.Name,
            null,
            null,
            null,
            playBase.Length > 0 ? links.ItemUrl(item.Id) : null,
            item.SeriesName,
            progress,
            upNext ? 0 : data?.PlaybackPositionTicks ?? 0,
            item.RunTimeTicks ?? 0,
            data?.LastPlayedDate,
            seriesTmdb,
            seriesTvdb,
            upNext);
    }

    /// <summary>
    /// Finds the next unplayed episode after <paramref name="current"/> inside the same series.
    /// Jellyfin's NextUp cannot answer this while the current episode is still marked unplayed, so
    /// the ordered episode list is used instead.
    /// </summary>
    async Task<ContinueWatchingItem?> ResolveNextEpisodeAsync(
        JellyfinItem current,
        WatchIndex? index,
        string playBase,
        DateTime? lastPlayed,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(current.SeriesId))
            return null;

        List<JellyfinItem> episodes = await GetSeriesEpisodesAsync(current.SeriesId, ct);
        int season = current.ParentIndexNumber ?? 0;
        int episode = current.IndexNumber ?? 0;
        logger.LogDebug(
            "Next-episode lookup: item={ItemId} series={SeriesId} S{Season}E{Episode} episodes={Count}",
            current.Id, current.SeriesId, season, episode, episodes.Count);

        foreach (JellyfinItem candidate in episodes
            .Where(e => e.ParentIndexNumber is > 0 && e.IndexNumber is > 0)
            .OrderBy(e => e.ParentIndexNumber)
            .ThenBy(e => e.IndexNumber))
        {
            int candidateSeason = candidate.ParentIndexNumber!.Value;
            int candidateEpisode = candidate.IndexNumber!.Value;
            if (!IsAfter(candidateSeason, candidateEpisode, season, episode))
                continue;

            return MapItem(candidate, index, playBase, upNext: true, seriesIdOverride: current.SeriesId) with { LastPlayed = lastPlayed };
        }

        logger.LogDebug("No following episode for {ItemId} in series {SeriesId}", current.Id, current.SeriesId);
        return null;
    }

    async Task<List<JellyfinItem>> GetSeriesEpisodesAsync(string seriesId, CancellationToken ct)
    {
        if (_episodeCache.TryGetValue(seriesId, out (DateTime BuiltAt, List<JellyfinItem> Episodes) cached) &&
            DateTime.UtcNow - cached.BuiltAt < EpisodeLifetime)
        {
            return cached.Episodes;
        }

        JellyfinItemsResult result = await client.GetSeriesEpisodesAsync(seriesId, ct: ct);
        _episodeCache[seriesId] = (DateTime.UtcNow, result.Items);
        return result.Items;
    }

    static bool IsAfter(int candidateSeason, int candidateEpisode, int season, int episode) =>
        candidateSeason > season || (candidateSeason == season && candidateEpisode > episode);

    /// <summary>
    /// True when a later episode has been played since the in-progress one, so the shelf should
    /// follow the viewer's latest position instead of an older half-watched episode.
    /// </summary>
    static bool HasMovedPast(
        ContinueWatchingItem inProgress,
        (DateTime PlayedAt, int Season, int Episode)? lastPlayed)
    {
        if (lastPlayed is not { } played)
            return false;

        if (!IsAfter(played.Season, played.Episode, inProgress.Key.SeasonNumber, inProgress.Key.EpisodeNumber))
            return false;

        return inProgress.LastPlayed is null || played.PlayedAt > inProgress.LastPlayed.Value;
    }

    /// <summary>Drops cached watch data so the shelf reflects a just-finished episode immediately.</summary>
    internal void InvalidateCache()
    {
        _indexes.Clear();
        _episodeCache.Clear();
    }

    public async Task<IReadOnlyList<WatchHistoryEntry>> GetHistoryAsync(
        string username,
        int limit = 100,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            return [];

        string? userId = await ResolveUserIdAsync(username, ct);
        if (userId is null)
            return [];

        try
        {
            JellyfinItemsResult response = await client.GetUserItemsAsync(userId, new JellyfinItemQuery
            {
                IncludeItemTypes = "Movie,Episode",
                Recursive = true,
                Filters = "IsPlayed",
                SortBy = "DatePlayed",
                SortOrder = "Descending",
                EnableUserData = true,
                Limit = limit,
                Fields = ["ProviderIds,UserData,SeriesInfo"]
            }, ct);

            List<WatchHistoryEntry> history = [];
            foreach (JellyfinItem item in response.Items)
            {
                JellyfinUserItemData? data = item.UserData;
                if (data?.LastPlayedDate is not DateTime playedAt)
                    continue;

                history.Add(new WatchHistoryEntry(
                    username,
                    ToKey(item),
                    item.SeriesName is { Length: > 0 } series ? $"{series} - {item.Name}" : item.Name,
                    playedAt,
                    data.PlayedPercentage / 100.0,
                    data.Played));
            }

            return history;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Jellyfin history lookup failed for {User}", username);
            return [];
        }
    }

    // Index building

    async Task<WatchIndex?> GetIndexAsync(string username, CancellationToken ct)
    {
        string? userId = await ResolveUserIdAsync(username, ct);
        if (userId is null)
            return null;

        // The index carries per-user watch state, so it must never be shared between users.
        if (_indexes.TryGetValue(username, out (DateTime BuiltAt, WatchIndex Index) cached) &&
            DateTime.UtcNow - cached.BuiltAt < IndexLifetime)
        {
            return cached.Index;
        }

        try
        {
            // Series first, so episodes can be mapped back to their series' TVDB id.
            JellyfinItemsResult seriesResponse = await client.GetUserItemsAsync(userId, new JellyfinItemQuery
            {
                IncludeItemTypes = "Series",
                Recursive = true,
                EnableUserData = true,
                Fields = ["ProviderIds,UserData"]
            }, ct);

            Dictionary<string, (int Tvdb, int Tmdb)> seriesById = [];
            WatchIndex index = new();

            foreach (JellyfinItem series in seriesResponse.Items)
            {
                int tvdb = ProviderId(series, "Tvdb");
                int tmdb = ProviderId(series, "Tmdb");
                seriesById[series.Id] = (tvdb, tmdb);
                index.SeriesById[series.Id] = (tmdb, tvdb);

                int total = series.RecursiveItemCount ?? series.ChildCount ?? 0;
                int unplayed = series.UserData?.UnplayedItemCount ?? 0;
                int watched = Math.Max(0, total - unplayed);

                if (total > 0)
                {
                    SeriesWatchState state = new(watched, total, null, null);
                    if (tvdb > 0)
                        index.SeriesByTvdb[tvdb] = state;
                    if (tmdb > 0)
                        index.SeriesByTmdb[tmdb] = state;
                }
            }

            JellyfinItemsResult items = await client.GetUserItemsAsync(userId, new JellyfinItemQuery
            {
                IncludeItemTypes = "Movie,Episode",
                Recursive = true,
                EnableUserData = true,
                Fields = ["ProviderIds,UserData,SeriesInfo"],
                Limit = 20000
            }, ct);

            foreach (JellyfinItem item in items.Items)
            {
                WatchState state = ToWatchState(item.UserData);

                if (string.Equals(item.Type, "Movie", StringComparison.OrdinalIgnoreCase))
                {
                    int tmdb = ProviderId(item, "Tmdb");
                    if (tmdb > 0)
                        index.MoviesByTmdb[tmdb] = state;
                }
                else if (string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase))
                {
                    int season = item.ParentIndexNumber ?? 0;
                    int episode = item.IndexNumber ?? 0;
                    int seriesTvdb = item.SeriesId is not null && seriesById.TryGetValue(item.SeriesId, out (int Tvdb, int Tmdb) ids)
                        ? ids.Tvdb
                        : ProviderId(item, "Tvdb");

                    if (seriesTvdb > 0 && season > 0)
                        index.EpisodesByKey[(seriesTvdb, season, episode)] = state;

                    if (item.SeriesId is { Length: > 0 } seriesId &&
                        state.IsPlayed &&
                        state.LastPlayed is DateTime playedAt &&
                        (!index.LastPlayedEpisodeBySeriesId.TryGetValue(seriesId, out (DateTime PlayedAt, int Season, int Episode) previous) ||
                         playedAt > previous.PlayedAt))
                    {
                        index.LastPlayedEpisodeBySeriesId[seriesId] = (playedAt, season, episode);
                    }
                }
            }

            _indexes[username] = (DateTime.UtcNow, index);
            return index;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Jellyfin watch index build failed for {User}", username);
            return _indexes.TryGetValue(username, out (DateTime BuiltAt, WatchIndex Index) stale) ? stale.Index : null;
        }
    }

    async Task<string?> ResolveUserIdAsync(string username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        await _lock.WaitAsync(ct);
        try
        {
            if (_userIds.TryGetValue(username, out string? cached) && cached is not null)
                return cached;

            List<JellyfinUser> users = await client.GetUsersAsync(ct);
            JellyfinUser? match = users.FirstOrDefault(u =>
                string.Equals(u.Name, username, StringComparison.OrdinalIgnoreCase));

            string? id = match?.Id;
            _userIds[username] = id;
            return id;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Jellyfin user lookup failed for {User}", username);
            _userIds[username] = null;
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    static MediaKey ToKey(JellyfinItem item)
    {
        int tmdb = ProviderId(item, "Tmdb");
        int tvdb = ProviderId(item, "Tvdb");
        int season = item.ParentIndexNumber ?? 0;
        int episode = item.IndexNumber ?? 0;
        MediaDomain domain = string.Equals(item.Type, "Movie", StringComparison.OrdinalIgnoreCase)
            ? TvMediaDomains.Movie
            : TvMediaDomains.Tv;
        return new MediaKey(domain, tmdb, tvdb, season, episode);
    }

    static WatchState ToWatchState(JellyfinUserItemData? data) => data is null
        ? WatchState.Unwatched
        : new WatchState(data.Played, data.PlayedPercentage, data.PlaybackPositionTicks, data.LastPlayedDate, data.PlayCount);

    static int ProviderId(JellyfinItem item, string key) =>
        item.ProviderIds.TryGetValue(key, out string? value) && int.TryParse(value, out int id) ? id : 0;
}
