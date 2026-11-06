using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Arrk.Core.Playback;
using Arrk.Downloads.Abstractions;
using Arrk.Media.Abstractions;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Models;

namespace Arrk.Tvarr.Services;

/// <summary>
/// Resolves a torrent hash to a movie/episode through the Radarr and Sonarr queues. Results are
/// cached briefly because download progress polling can ask about the same hash repeatedly while
/// the arrs are the source of truth.
/// </summary>
public sealed class TvDownloadEntityResolver(
    RadarrService radarr,
    SonarrService sonarr,
    TimeProvider time,
    ILogger<TvDownloadEntityResolver> logger) : IMediaDownloadEntityResolver
{
    static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    static readonly TimeSpan NegativeCacheLifetime = TimeSpan.FromMinutes(1);
    static readonly TimeSpan QueueCacheLifetime = TimeSpan.FromSeconds(20);

    readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _radarrQueueLock = new(1, 1);
    readonly SemaphoreSlim _sonarrQueueLock = new(1, 1);
    List<RadarrQueueItemDto>? _radarrQueue;
    List<SonarrQueueItemDto>? _sonarrQueue;
    DateTime _radarrQueueFetchedAt;
    DateTime _sonarrQueueFetchedAt;

    public async Task<MediaEntityRef?> ResolveAsync(string torrentHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(torrentHash))
            return null;

        DateTime now = time.GetUtcNow().UtcDateTime;
        if (_cache.TryGetValue(torrentHash, out CacheEntry? cached) && cached.ExpiresAt > now)
            return cached.Entity;

        if (_cache.Count > 2000)
        {
            foreach ((string key, CacheEntry entry) in _cache)
            {
                if (entry.ExpiresAt <= now)
                    _cache.TryRemove(key, out _);
            }
        }

        MediaEntityRef? entity;
        try
        {
            entity = await ResolveMovieAsync(torrentHash, ct) ?? await ResolveEpisodeAsync(torrentHash, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve download {Hash} to a media entity", torrentHash);
            entity = null;
        }

        _cache[torrentHash] = new CacheEntry(
            entity,
            now + (entity is null ? NegativeCacheLifetime : CacheLifetime));
        return entity;
    }

    async Task<MediaEntityRef?> ResolveMovieAsync(string torrentHash, CancellationToken ct)
    {
        List<RadarrQueueItemDto> queue = await GetRadarrQueueAsync(ct);
        RadarrQueueItemDto? item = queue.FirstOrDefault(x =>
            x.MovieId > 0 &&
            string.Equals(x.DownloadId, torrentHash, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return null;

        TvMovie? movie = await radarr.GetMovie(item.MovieId, ct);
        if (movie is null || movie.TmdbId <= 0)
            return null;

        return new MediaEntityRef(
            "tv",
            TvMediaDomains.MovieDomain,
            MediaItemKeys.Movie(movie.TmdbId),
            movie.Title,
            movie.Year > 0 ? movie.Year.ToString() : null,
            movie.PosterUrl,
            movie.FanartUrl,
            Url: "/movies");
    }

    async Task<MediaEntityRef?> ResolveEpisodeAsync(string torrentHash, CancellationToken ct)
    {
        List<SonarrQueueItemDto> queue = await GetSonarrQueueAsync(ct);
        SonarrQueueItemDto? item = queue.FirstOrDefault(x =>
            x.SeriesId > 0 &&
            string.Equals(x.DownloadId, torrentHash, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return null;

        TvSeries? series = await sonarr.GetSeries(item.SeriesId, ct);
        if (series is null)
            return null;

        string seriesKey = MediaItemKeys.Series(series.TvdbId, series.TmdbId);
        if (string.IsNullOrEmpty(seriesKey))
            return null;

        if (item.EpisodeId <= 0)
        {
            return new MediaEntityRef(
                "tv",
                TvMediaDomains.TvDomain,
                seriesKey,
                series.Title,
                null,
                series.PosterUrl,
                series.FanartUrl,
                Url: "/shows");
        }

        List<TvEpisode> episodes = await sonarr.GetEpisodes(item.SeriesId, ct);
        TvEpisode? episode = episodes.FirstOrDefault(e => e.Id == item.EpisodeId);
        if (episode is null)
        {
            return new MediaEntityRef(
                "tv",
                TvMediaDomains.TvDomain,
                seriesKey,
                series.Title,
                null,
                series.PosterUrl,
                series.FanartUrl,
                Url: "/shows");
        }

        string seasonKey = MediaItemKeys.Season(seriesKey, episode.SeasonNumber);
        return new MediaEntityRef(
            "tv",
            TvMediaDomains.TvDomain,
            MediaItemKeys.Episode(seriesKey, episode.SeasonNumber, episode.EpisodeNumber),
            series.Title,
            $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}",
            series.PosterUrl,
            series.FanartUrl,
            seasonKey,
            [seasonKey, seriesKey],
            Url: "/shows");
    }

    async Task<List<RadarrQueueItemDto>> GetRadarrQueueAsync(CancellationToken ct)
    {
        if (_radarrQueue is not null && time.GetUtcNow().UtcDateTime - _radarrQueueFetchedAt < QueueCacheLifetime)
            return _radarrQueue;

        await _radarrQueueLock.WaitAsync(ct);
        try
        {
            if (_radarrQueue is not null && time.GetUtcNow().UtcDateTime - _radarrQueueFetchedAt < QueueCacheLifetime)
                return _radarrQueue;

            _radarrQueue = await radarr.GetQueue(ct);
            _radarrQueueFetchedAt = time.GetUtcNow().UtcDateTime;
            return _radarrQueue;
        }
        finally
        {
            _radarrQueueLock.Release();
        }
    }

    async Task<List<SonarrQueueItemDto>> GetSonarrQueueAsync(CancellationToken ct)
    {
        if (_sonarrQueue is not null && time.GetUtcNow().UtcDateTime - _sonarrQueueFetchedAt < QueueCacheLifetime)
            return _sonarrQueue;

        await _sonarrQueueLock.WaitAsync(ct);
        try
        {
            if (_sonarrQueue is not null && time.GetUtcNow().UtcDateTime - _sonarrQueueFetchedAt < QueueCacheLifetime)
                return _sonarrQueue;

            _sonarrQueue = await sonarr.GetQueue(ct);
            _sonarrQueueFetchedAt = time.GetUtcNow().UtcDateTime;
            return _sonarrQueue;
        }
        finally
        {
            _sonarrQueueLock.Release();
        }
    }

    sealed record CacheEntry(MediaEntityRef? Entity, DateTime ExpiresAt);
}
