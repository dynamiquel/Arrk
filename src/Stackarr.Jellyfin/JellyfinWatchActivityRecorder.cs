using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Enums;
using Stackarr.Core.Playback;

namespace Stackarr.Jellyfin;

/// <summary>
/// Records Jellyfin playback into Stackarr's durable history. Used by both the session poller
/// (external clients) and the in-site player, so a play has one identity model regardless of which
/// surface produced it.
/// </summary>
public sealed class JellyfinWatchActivityRecorder(
    JellyfinClient client,
    WatchActivityService watch,
    ILogger<JellyfinWatchActivityRecorder> logger)
{
    readonly ConcurrentDictionary<string, (int Tmdb, int Tvdb)> _series = new(StringComparer.Ordinal);

    public async Task RecordAsync(
        string username,
        JellyfinItem item,
        string source,
        string? device,
        string? clientName,
        string? ipAddress,
        string? sessionKey,
        long positionTicks,
        bool completed,
        DateTime? startedAt = null,
        DateTime? lastSeenAt = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(item.Id))
            return;

        JellyfinActivityIdentity? identity = await ResolveAsync(item, ct);
        if (identity is null)
            return;

        DateTime now = DateTime.UtcNow;
        long runtime = item.RunTimeTicks ?? 0;
        double progress = runtime > 0 ? Math.Clamp(positionTicks / (double)runtime, 0, 1) : 0;
        bool finished = completed || (runtime > 0 && positionTicks >= runtime * 0.9);

        await watch.RecordAsync(new WatchActivityRecord
        {
            Username = username,
            ProviderKey = WatchActivityServices.Tv,
            Domain = identity.Domain,
            ItemType = identity.ItemType,
            ItemKey = identity.ItemKey,
            ParentItemKey = identity.ParentItemKey,
            Title = identity.Title,
            Subtitle = identity.Subtitle,
            Source = source,
            Device = device,
            Client = clientName,
            IpAddress = ipAddress,
            SessionKey = sessionKey,
            StartedAt = startedAt ?? now,
            LastSeenAt = lastSeenAt ?? now,
            PositionTicks = positionTicks,
            RuntimeTicks = runtime,
            Progress = progress,
            Completed = finished,
            TmdbId = identity.TmdbId,
            TvdbId = identity.TvdbId,
            SeasonNumber = identity.SeasonNumber,
            EpisodeNumber = identity.EpisodeNumber
        }, ct);
    }

    /// <summary>Imports a user's already-played Jellyfin items (device unknown) once on startup.</summary>
    public async Task BackfillAsync(string username, CancellationToken ct = default)
    {
        string? userId = await client.GetUserIdByNameAsync(username, ct);
        if (string.IsNullOrEmpty(userId))
            return;

        JellyfinItemsResult response = await client.GetUserItemsAsync(userId, new JellyfinItemQuery
        {
            IncludeItemTypes = "Movie,Episode",
            Recursive = true,
            Filters = "IsPlayed",
            SortBy = "DatePlayed",
            SortOrder = "Descending",
            EnableUserData = true,
            Limit = 200,
            Fields = ["ProviderIds,UserData,SeriesInfo"]
        }, ct);

        foreach (JellyfinItem item in response.Items)
        {
            DateTime? playedAt = item.UserData?.LastPlayedDate;
            if (playedAt is null)
                continue;

            string sessionKey = $"jellyfin-history:{username}:{item.Id}:{playedAt.Value.Ticks}";
            if (await watch.HasEntryAsync(sessionKey, ct))
                continue;

            await RecordAsync(
                username,
                item,
                WatchActivitySources.Jellyfin,
                device: null,
                clientName: null,
                ipAddress: null,
                sessionKey: sessionKey,
                positionTicks: item.RunTimeTicks ?? 0,
                completed: true,
                startedAt: playedAt,
                lastSeenAt: playedAt,
                ct);
        }

        logger.LogDebug("Imported Jellyfin watch history for {User}", username);
    }

    async Task<JellyfinActivityIdentity?> ResolveAsync(JellyfinItem item, CancellationToken ct)
    {
        if (item.Type == "Movie")
        {
            int tmdb = ProviderId(item, "Tmdb");
            string key = MediaItemKeys.Movie(tmdb);
            return string.IsNullOrEmpty(key)
                ? null
                : new JellyfinActivityIdentity(TvMediaDomains.Movie, "movie", key, null, item.Name, null, tmdb, 0, 0, 0);
        }

        if (item.Type == "Series")
        {
            (int tmdb, int tvdb) = ProviderIds(item);
            string key = MediaItemKeys.Series(tvdb, tmdb);
            return string.IsNullOrEmpty(key)
                ? null
                : new JellyfinActivityIdentity(TvMediaDomains.Tv, "series", key, null, item.Name, null, tmdb, tvdb, 0, 0);
        }

        if (item.Type != "Episode")
            return null;

        (int seriesTmdb, int seriesTvdb) = (0, 0);
        if (!string.IsNullOrEmpty(item.SeriesId))
            (seriesTmdb, seriesTvdb) = await ResolveSeriesAsync(item.SeriesId, ct);
        if (seriesTvdb == 0)
        {
            (_, seriesTvdb) = ProviderIds(item);
            if (seriesTvdb == 0)
                seriesTvdb = ProviderId(item, "Tvdb");
        }

        string seriesKey = MediaItemKeys.Series(seriesTvdb, seriesTmdb);
        if (string.IsNullOrEmpty(seriesKey))
            return null;

        int season = item.ParentIndexNumber ?? 0;
        int episode = item.IndexNumber ?? 0;
        string episodeKey = MediaItemKeys.Episode(seriesKey, season, episode);
        string seasonKey = MediaItemKeys.Season(seriesKey, season);
        return new JellyfinActivityIdentity(
            TvMediaDomains.Tv,
            "episode",
            string.IsNullOrEmpty(episodeKey) ? seriesKey : episodeKey,
            string.IsNullOrEmpty(seasonKey) ? seriesKey : seasonKey,
            item.Name,
            item.SeriesName,
            seriesTmdb,
            seriesTvdb,
            season,
            episode);
    }

    async Task<(int Tmdb, int Tvdb)> ResolveSeriesAsync(string seriesId, CancellationToken ct)
    {
        if (_series.TryGetValue(seriesId, out (int Tmdb, int Tvdb) cached))
            return cached;

        try
        {
            JellyfinItem? series = await client.GetItemAsync(seriesId, ct);
            (int tmdb, int tvdb) = series is null ? (0, 0) : ProviderIds(series);
            _series[seriesId] = (tmdb, tvdb);
            return (tmdb, tvdb);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve Jellyfin series {SeriesId}", seriesId);
            return (0, 0);
        }
    }

    static (int Tmdb, int Tvdb) ProviderIds(JellyfinItem item) =>
        (ProviderId(item, "Tmdb"), ProviderId(item, "Tvdb"));

    static int ProviderId(JellyfinItem item, string key) =>
        item.ProviderIds.TryGetValue(key, out string? value) && int.TryParse(value, out int id) ? id : 0;

    sealed record JellyfinActivityIdentity(
        MediaDomain Domain,
        string ItemType,
        string ItemKey,
        string? ParentItemKey,
        string Title,
        string? Subtitle,
        int TmdbId,
        int TvdbId,
        int SeasonNumber,
        int EpisodeNumber);
}
