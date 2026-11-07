using Arrk.Core.Requests;
using Microsoft.Extensions.Logging;
using Arrk.Core.Enums;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Notifications;
using Arrk.Core.Playback;
using Arrk.Core.Services;
using Arrk.Media.Abstractions;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Models;

namespace Arrk.Tvarr.Services;

public partial class SonarrService : ArrService, ISeriesLibrary, ISeriesLibraryWriter
{
    readonly SonarrClient _client;
    readonly ILogger<SonarrService> _logger;
    readonly IEventStore? _eventStore;
    readonly IEventBus? _eventBus;
    readonly ITorrentDownloader? _torrents;
    protected override ArrClient Client => _client;

    public event EventHandler<SeriesChangedEventArgs>? SeriesUpdated;

    public SonarrService(
        SonarrClient client,
        ILogger<SonarrService> logger,
        IEventStore? eventStore = null,
        IEventBus? eventBus = null,
        ITorrentDownloader? torrents = null)
    {
        _client = client;
        _logger = logger;
        _eventStore = eventStore;
        _eventBus = eventBus;
        _torrents = torrents;
    }

    public async Task<List<TvSeries>> GetAllSeries(CancellationToken ct = default)
    {
        List<SeriesDto> series = await _client.GetSeries(ct);
        List<TvSeries> result = new List<TvSeries>();
        foreach (SeriesDto s in series)
        {
            result.Add(MapToTvSeries(s));
        }
        return result;
    }

    public async Task<TvSeries?> GetSeries(int id, CancellationToken ct = default)
    {
        SeriesDto? series = await _client.GetSeries(id, ct);
        return series == null ? null : MapToTvSeries(series);
    }

    public async Task<List<SeriesLookupDto>> SearchSeries(string query, CancellationToken ct = default)
    {
        return await _client.SearchSeries(query, ct);
    }

    public async Task<List<TvSeason>> GetSeasons(int seriesId, CancellationToken ct = default)
    {
        SeriesDto? series = await _client.GetSeries(seriesId, ct);
        if (series == null)
            return [];
        return series.Seasons.Select(s => new TvSeason
        {
            SeasonNumber = s.SeasonNumber,
            Monitored = s.Monitored,
            EpisodeCount = s.Statistics?.EpisodeCount ?? 0,
            DownloadedEpisodeCount = s.Statistics?.EpisodeFileCount ?? 0
        }).ToList();
    }

    public async Task<List<TvEpisode>> GetEpisodes(int seriesId, CancellationToken ct = default)
    {
        List<EpisodeDto> episodes = await _client.GetEpisodes(seriesId, ct);
        (Dictionary<int, EpisodeFileDto> byEpisode, Dictionary<int, EpisodeFileDto> byFile) =
            await BuildEpisodeFileIndex(seriesId, ct);

        return episodes.Select(e =>
        {
            EpisodeFileDto? file = null;
            byEpisode.TryGetValue(e.Id, out file);
            if (file is null && e.EpisodeFileId > 0)
                byFile.TryGetValue(e.EpisodeFileId, out file);

            return MapEpisode(e, file);
        }).ToList();
    }

    public async Task<List<EpisodeFileDto>> GetEpisodeFiles(int seriesId, CancellationToken ct = default)
    {
        return await _client.GetEpisodeFiles(seriesId, ct);
    }

    /// <summary>Recent Sonarr history, newest first (used by the media import monitor).</summary>
    public async Task<List<HistoryDto>> GetRecentHistory(int pageSize = 200, CancellationToken ct = default)
    {
        return await _client.GetRecentHistory(pageSize, ct);
    }

    async Task<(Dictionary<int, EpisodeFileDto> ByEpisode, Dictionary<int, EpisodeFileDto> ByFile)>
        BuildEpisodeFileIndex(int seriesId, CancellationToken ct)
    {
        Dictionary<int, EpisodeFileDto> byEpisode = [];
        Dictionary<int, EpisodeFileDto> byFile = [];
        try
        {
            List<EpisodeFileDto> files = await _client.GetEpisodeFiles(seriesId, ct);
            foreach (EpisodeFileDto file in files)
            {
                byFile[file.Id] = file;
                foreach (int episodeId in file.EpisodeIds)
                    byEpisode[episodeId] = file;
            }
        }
        catch
        {
            // Episode-level quality is best-effort; a missing scan must not break the list.
        }
        return (byEpisode, byFile);
    }

    static TvEpisode MapEpisode(EpisodeDto e, EpisodeFileDto? file)
    {
        QualityInfo? quality = null;
        MediaFileInfo? fileInfo = null;
        if (file != null)
        {
            quality = MediaInfoMapper.QualityFrom(file.Quality, file.MediaInfo);
            fileInfo = new()
            {
                Path = file.Path,
                SizeBytes = file.Size,
                DateAdded = file.DateAdded,
                Quality = quality,
                Technical = MediaInfoMapper.TechnicalFrom(file.MediaInfo, file.Path, file.Size, file.DateAdded)
            };
        }

        return new TvEpisode
        {
            Id = e.Id,
            SeriesId = e.SeriesId,
            EpisodeFileId = file?.Id ?? 0,
            SeasonNumber = e.SeasonNumber,
            EpisodeNumber = e.EpisodeNumber,
            Title = e.Title,
            Overview = e.Overview ?? "",
            Monitored = e.Monitored,
            HasFile = e.HasFile,
            AirDate = e.AirDate != null ? DateTime.TryParse(e.AirDate, out DateTime ad) ? ad : null : null,
            ImageUrl = ResolveEpisodeImage(e.Images),
            Quality = quality,
            File = fileInfo
        };
    }

    static string ResolveEpisodeImage(List<ImageDto>? images)
    {
        return images?.FirstOrDefault(i => i.CoverType is "still" or "screenshot")?.RemoteUrl
            ?? images?.FirstOrDefault(i => i.CoverType is "banner" or "poster")?.RemoteUrl
            ?? string.Empty;
    }

    public async Task DeleteSeries(int id, bool deleteFiles = false, CancellationToken ct = default)
    {
        TvSeries? series = null;
        try { series = await GetSeries(id, ct); } catch { /* best effort for the removed event */ }

        // Sonarr's delete endpoint never touches the download client, so in-progress (or
        // still-seeding) episode torrents would keep running after the series is gone. Delete the
        // series' downloads from Transmission first, using the queue (in-progress) and history
        // (finished) to map them to torrent hashes.
        await RemoveMediaDownloadsAsync(id, deleteFiles, ct);

        await _client.DeleteSeries(id, deleteFiles, ct);
        _logger.LogInformation("Deleted series {Id}, deleteFiles={DeleteFiles}", id, deleteFiles);

        if (series is not null && _eventBus is not null)
        {
            Dictionary<string, string> providerIds = ProviderIdMap.Build(
                (TvProviderIds.Tmdb, series.TmdbId > 0 ? series.TmdbId.ToString() : null),
                (TvProviderIds.Tvdb, series.TvdbId > 0 ? series.TvdbId.ToString() : null),
                (TvProviderIds.Imdb, series.ImdbId));
            await _eventBus.Publish(new MediaRemovedEvent(TvMediaDomains.Tv, providerIds, series.Title), ct);
        }
    }

    async Task RemoveMediaDownloadsAsync(int seriesId, bool deleteFiles, CancellationToken ct)
    {
        if (_torrents is null)
            return;

        HashSet<string> hashes = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (SonarrQueueItemDto item in await _client.GetQueue(ct, pageSize: 1000))
            {
                if (item.SeriesId == seriesId && !string.IsNullOrWhiteSpace(item.DownloadId))
                    hashes.Add(item.DownloadId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Sonarr queue for series {SeriesId}", seriesId);
        }

        try
        {
            foreach (HistoryDto history in await _client.GetSeriesHistory(seriesId, ct))
            {
                if (!string.IsNullOrWhiteSpace(history.DownloadId))
                    hashes.Add(history.DownloadId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Sonarr history for series {SeriesId}", seriesId);
        }

        await DeleteTorrentsAsync(hashes, deleteFiles, ct);
    }

    async Task DeleteTorrentsAsync(HashSet<string> hashes, bool deleteFiles, CancellationToken ct)
    {
        if (_torrents is null || hashes.Count == 0)
            return;

        List<TorrentItem> active;
        try
        {
            active = await _torrents.GetTorrents("all", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list torrents to clean up for deleted series");
            return;
        }

        TorrentFileCleanup cleanup = deleteFiles ? TorrentFileCleanup.DeleteFiles : TorrentFileCleanup.KeepFiles;
        foreach (TorrentItem torrent in active.Where(t => hashes.Contains(t.Hash)))
        {
            try
            {
                await _torrents.DeleteTorrent(torrent.Hash, cleanup, ct);
                _logger.LogInformation("Deleted torrent {Hash} ({Name})", torrent.Hash, torrent.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete torrent {Hash}", torrent.Hash);
            }
        }
    }

    public async Task DeleteEpisode(int episodeFileId, CancellationToken ct = default)
    {
        await _client.DeleteEpisodeFile(episodeFileId, ct);
        _logger.LogInformation("Deleted episode file {Id}", episodeFileId);
    }

    public async Task SearchAndAddSeries(
        int tvdbId,
        string title,
        int? qualityProfileId = null,
        bool monitored = true,
        bool seasonFolder = true,
        bool searchForSeries = true,
        CancellationToken ct = default)
    {
        int profileId = qualityProfileId ?? await GetDefaultQualityProfileId(ct);
        string rootFolder = await GetDefaultRootFolder(ct);

        SeriesCreateRequest series = new SeriesCreateRequest
        {
            TvdbId = tvdbId,
            Title = title,
            QualityProfileId = profileId,
            Path = $"{rootFolder}/{tvdbId}",
            Monitored = monitored,
            SeasonFolder = seasonFolder
        };
        SeriesDto created = await _client.CreateSeries(series, ct);
        _logger.LogInformation("Added series {Title} (TVDB {TvdbId})", title, tvdbId);

        await RecordMediaAddedAsync(created.Title, created.Year, created.TvdbId, created.Images, ct);

        if (searchForSeries && created.Id > 0)
        {
            await TriggerInitialSearch(created.Id, ct);
        }
    }

    public async Task<int> AddSeriesWithOptions(
        int tvdbId,
        string title,
        int qualityProfileId,
        HashSet<int>? monitoredSeasons = null,
        bool monitorFutureSeasons = true,
        bool searchForSeries = true,
        CancellationToken ct = default)
    {
        string rootFolder = await GetDefaultRootFolder(ct);

        SeriesCreateRequest series = new SeriesCreateRequest
        {
            TvdbId = tvdbId,
            Title = title,
            QualityProfileId = qualityProfileId,
            Path = $"{rootFolder}/{tvdbId}",
            Monitored = true,
            SeasonFolder = true
        };
        SeriesDto created = await _client.CreateSeries(series, ct);
        _logger.LogInformation(
            "Added series {Title} (TVDB {TvdbId}), id={Id}",
            created.Title, tvdbId, created.Id);

        await RecordMediaAddedAsync(created.Title, created.Year, created.TvdbId, created.Images, ct);

        if (monitoredSeasons != null || !monitorFutureSeasons)
        {
            await ApplySeasonMonitoring(created.Id, monitoredSeasons, monitorFutureSeasons, ct);
        }

        if (searchForSeries)
        {
            await TriggerInitialSearch(created.Id, ct);
        }

        return created.Id;
    }

    async Task TriggerInitialSearch(int seriesId, CancellationToken ct)
    {
        try
        {
            await CommandSearchSeries(seriesId, ct);
            _logger.LogInformation("Triggered initial search for series {Id}", seriesId);
        }
        catch (Exception ex)
        {
            // The series is still added; a failed initial search should not fail the add.
            _logger.LogWarning(ex, "Initial search for series {Id} failed", seriesId);
        }
    }

    public async Task ApplySeasonMonitoring(
        int seriesId,
        HashSet<int>? monitoredSeasons = null,
        bool monitorFutureSeasons = true,
        CancellationToken ct = default)
    {
        SeriesDto? series = await _client.GetSeries(seriesId, ct);
        if (series == null)
            return;

        if (monitoredSeasons != null)
        {
            foreach (SeasonDto season in series.Seasons.Where(s => s.SeasonNumber > 0))
            {
                season.Monitored = monitoredSeasons.Contains(season.SeasonNumber);
            }
        }

        await _client.UpdateSeries(series, ct);

        if (monitoredSeasons != null)
        {
            List<EpisodeDto> episodes = await _client.GetEpisodes(seriesId, ct);
            List<EpisodeDto> episodesToUpdate = episodes
                .Where(e => e.SeasonNumber > 0 && monitoredSeasons.Contains(e.SeasonNumber) != e.Monitored)
                .ToList();
            foreach (EpisodeDto ep in episodesToUpdate)
            {
                await _client.UpdateEpisodeMonitored(ep.Id, monitoredSeasons.Contains(ep.SeasonNumber), ct);
            }
        }

        _logger.LogInformation(
            "Updated season monitoring for series {Id}, futureSeasons={Future}",
            seriesId, monitorFutureSeasons);
    }

    public async Task UpdateSeriesMonitoring(
        int tvdbId,
        int qualityProfileId,
        HashSet<int>? monitoredSeasons = null,
        bool monitorFutureSeasons = true,
        CancellationToken ct = default)
    {
        List<SeriesDto> allSeries = await _client.GetSeries(ct);
        SeriesDto? series = allSeries.FirstOrDefault(s => s.TvdbId == tvdbId);
        if (series == null)
        {
            _logger.LogWarning("Series with TVDB ID {TvdbId} not found for update", tvdbId);
            return;
        }

        series.QualityProfileId = qualityProfileId;

        if (monitoredSeasons != null)
        {
            foreach (SeasonDto season in series.Seasons.Where(s => s.SeasonNumber > 0))
            {
                season.Monitored = monitoredSeasons.Contains(season.SeasonNumber);
            }
        }

        await _client.UpdateSeries(series, ct);

        if (monitoredSeasons != null)
        {
            List<EpisodeDto> episodes = await _client.GetEpisodes(series.Id, ct);
            List<EpisodeDto> episodesToUpdate = episodes
                .Where(e => e.SeasonNumber > 0 && monitoredSeasons.Contains(e.SeasonNumber) != e.Monitored)
                .ToList();
            foreach (EpisodeDto ep in episodesToUpdate)
            {
                await _client.UpdateEpisodeMonitored(ep.Id, monitoredSeasons.Contains(ep.SeasonNumber), ct);
            }
        }

        _logger.LogInformation(
            "Updated series {Id} (TVDB {TvdbId}): profile={Profile}, futureSeasons={Future}",
            series.Id, tvdbId, qualityProfileId, monitorFutureSeasons);
    }

    public override async Task<string> GetDefaultRootFolder(CancellationToken ct = default)
    {
        List<RootFolder> folders = await _client.GetRootFolders(ct);
        return folders.FirstOrDefault()?.Path ?? "/tv";
    }

    async Task RecordMediaAddedAsync(string title, int year, int providerId, List<ImageDto>? images, CancellationToken ct)
    {
        if (_eventStore is null)
            return;

        string display = string.IsNullOrWhiteSpace(title) ? providerId.ToString() : title;
        string body = $"{display} ({year}) was added to the library";
        string seriesKey = MediaItemKeys.Series(providerId, 0);
        MediaEntityRef? entity = seriesKey.Length > 0
            ? new MediaEntityRef(
                "tv",
                TvMediaDomains.TvDomain,
                seriesKey,
                display,
                year > 0 ? year.ToString() : null,
                Url: "/shows")
            : null;

        try
        {
            await _eventStore.RecordAsync(new SystemEventRecord
            {
                Category = EventCategory.Media,
                EventType = "media.tv.added",
                Severity = EventSeverity.Info,
                Message = body,
                Source = providerId.ToString(),
                SourceName = display,
                Notification = new NotificationMessage
                {
                    Key = MediaNotificationTopics.MediaAdded,
                    Title = "Series added",
                    Body = body,
                    Severity = EventSeverity.Info,
                    Source = providerId.ToString(),
                    SourceName = display,
                    Timestamp = DateTime.UtcNow,
                    Style = MediaArtStyle(images),
                    Fields =
                    [
                        new("Type", "Series"),
                        new("Year", year.ToString()),
                        new("Monitored", "true")
                    ],
                    Entity = entity,
                    Audience = entity is null
                        ? NotificationAudience.Broadcast
                        : NotificationAudience.BroadcastAndSubscribers
                }
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record media-added event for {Title}", display);
        }
    }

    /// <summary>Poster as a thumbnail and fanart as the large image, when the arr supplies them.</summary>
    static NotificationStyle? MediaArtStyle(List<ImageDto>? images)
    {
        string poster = ResolveImageUrl(images, "poster");
        string fanart = ResolveImageUrl(images, "fanart");
        if (string.IsNullOrWhiteSpace(fanart))
            fanart = ResolveImageUrl(images, "background");

        if (string.IsNullOrWhiteSpace(poster) && string.IsNullOrWhiteSpace(fanart))
            return null;

        return new NotificationStyle { ThumbnailUrl = poster, ImageUrl = fanart };
    }

    protected override async Task ToggleMonitoredCore(int id, bool monitored, CancellationToken ct)
    {
        SeriesDto? series = await _client.GetSeries(id, ct);
        if (series == null)
            return;
        series.Monitored = monitored;
        await _client.UpdateSeries(series, ct);
        _logger.LogInformation("Series {Id} monitored={Monitored}", id, monitored);
    }

    public async Task ToggleSeasonMonitored(int seriesId, int seasonNumber, bool monitored, CancellationToken ct = default)
    {
        SeriesDto? series = await _client.GetSeries(seriesId, ct);
        if (series == null)
            return;
        SeasonDto? season = series.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
        if (season == null)
            return;
        season.Monitored = monitored;
        await _client.UpdateSeries(series, ct);

        List<EpisodeDto> episodes = await _client.GetEpisodes(seriesId, ct);
        List<EpisodeDto> seasonEpisodes = episodes.Where(e => e.SeasonNumber == seasonNumber && e.Monitored != monitored).ToList();
        foreach (EpisodeDto ep in seasonEpisodes)
        {
            await _client.UpdateEpisodeMonitored(ep.Id, monitored, ct);
        }

        _logger.LogInformation(
            "Series {SeriesId} season {Season} monitored={Monitored}, updated {Count} episodes",
            seriesId, seasonNumber, monitored, seasonEpisodes.Count);
    }

    public async Task ToggleEpisodeMonitored(int episodeId, bool monitored, CancellationToken ct = default)
    {
        await _client.UpdateEpisodeMonitored(episodeId, monitored, ct);
        _logger.LogInformation("Episode {EpisodeId} monitored={Monitored}", episodeId, monitored);
    }

    /// <summary>Runs Sonarr's native series search ("Search Monitored").</summary>
    public async Task CommandSearchSeries(int seriesId, CancellationToken ct = default)
    {
        await _client.CommandSearchSeries(seriesId, ct);
        _logger.LogInformation("Triggered Sonarr series search for series {SeriesId}", seriesId);
    }

    /// <summary>Runs Sonarr's native season search.</summary>
    public async Task CommandSearchSeason(int seriesId, int seasonNumber, CancellationToken ct = default)
    {
        await _client.CommandSearchSeason(seriesId, seasonNumber, ct);
        _logger.LogInformation(
            "Triggered Sonarr season search for series {SeriesId} season {Season}",
            seriesId, seasonNumber);
    }

    /// <summary>Runs Sonarr's native episode search for one episode.</summary>
    public async Task CommandSearchEpisode(int episodeId, CancellationToken ct = default)
    {
        await _client.CommandSearchEpisodes([episodeId], ct);
        _logger.LogInformation("Triggered Sonarr episode search for episode {EpisodeId}", episodeId);
    }

    /// <summary>
    /// Runs Sonarr's native "Search for Missing Episodes" command. Sonarr decides which episodes to
    /// search (monitored, aired, not already queued) and how (season pack or episode search).
    /// </summary>
    public async Task SearchMissingAsync(CancellationToken ct = default)
    {
        await _client.CommandSearchMissingEpisodes(ct);
        _logger.LogInformation("Triggered Sonarr missing-episode search");
    }

    /// <summary>Release candidates for Sonarr's interactive search.</summary>
    public async Task<List<ReleaseResourceDto>> GetReleases(
        int? episodeId = null,
        int? seriesId = null,
        int? seasonNumber = null,
        CancellationToken ct = default)
    {
        return await _client.GetReleases(episodeId, seriesId, seasonNumber, ct);
    }

    /// <summary>Grabs a chosen release through Sonarr's download client.</summary>
    public async Task GrabRelease(string guid, int indexerId, CancellationToken ct = default)
    {
        await _client.GrabRelease(guid, indexerId, ct);
        _logger.LogInformation("Grabbed release {Guid} from Sonarr indexer {IndexerId}", guid, indexerId);
    }

    public async Task<List<SonarrQueueItemDto>> GetQueue(CancellationToken ct = default)
    {
        return await _client.GetQueue(ct);
    }

    public async Task DeleteQueueItem(int queueItemId, bool blocklist = false, CancellationToken ct = default)
    {
        await _client.DeleteQueueItem(queueItemId, blocklist, removeFromClient: true, searchAgain: false, ct);
        _logger.LogInformation("Deleted queue item {Id}, blocklist={Blocklist}", queueItemId, blocklist);
    }

    public async Task DeleteQueueItemByHash(string hash, bool blocklist = true, CancellationToken ct = default)
    {
        List<SonarrQueueItemDto> queue = await _client.GetQueue(ct, pageSize: 1000);
        SonarrQueueItemDto? item = queue.FirstOrDefault(q => q.DownloadId.Equals(hash, StringComparison.OrdinalIgnoreCase));
        if (item != null)
        {
            await _client.DeleteQueueItem(item.Id, blocklist, removeFromClient: true, searchAgain: blocklist, ct);
            _logger.LogInformation("Deleted queue item {Id} for torrent {Hash}, blocklist={Blocklist}", item.Id, hash, blocklist);
        }
        else
        {
            _logger.LogWarning("No queue item found for torrent hash {Hash}", hash);
        }
    }

    /// <summary>
    /// Auto-resolves a removed torrent: traces it back to its episode/series, blocklists the failed
    /// release and triggers a fresh search. Prefers the live queue; falls back to history, which
    /// persists after the download has been removed from the client.
    /// </summary>
    public async Task<FailedDownloadResolution> ResolveFailedDownload(string hash, CancellationToken ct = default)
    {
        List<SonarrQueueItemDto> queue = await _client.GetQueue(ct, pageSize: 1000);
        SonarrQueueItemDto? item = queue.FirstOrDefault(
            q => q.DownloadId.Equals(hash, StringComparison.OrdinalIgnoreCase));

        if (item is not null)
        {
            await _client.DeleteQueueItem(item.Id, blocklist: true, removeFromClient: false, searchAgain: true, ct);
            _logger.LogInformation(
                "Sonarr queue item {Id} for torrent {Hash} removed, blocklisted and re-searching", item.Id, hash);
            return new FailedDownloadResolution(true, true, true, $"queue item {item.Id}");
        }

        List<HistoryDto> history = await _client.GetHistoryByDownloadId(hash, ct);
        HistoryDto? grabbed = history
            .Where(h => h.EventType is "grabbed" or "downloadFailed")
            .OrderByDescending(h => h.Date)
            .FirstOrDefault();
        if (grabbed is null)
        {
            _logger.LogWarning("No Sonarr queue item or grabbable history found for torrent {Hash}", hash);
            return new FailedDownloadResolution(false, false, false, "no queue item or history");
        }

        // Only the first failure needs marking; the release is already blocklisted otherwise.
        if (grabbed.EventType == "grabbed")
            await _client.MarkHistoryFailed(grabbed.Id, ct);

        bool searched = false;
        if (grabbed.EpisodeId is int episodeId and > 0)
        {
            await _client.CommandSearchEpisode(episodeId, ct);
            searched = true;
        }
        else if (grabbed.SeriesId is int seriesId and > 0)
        {
            await CommandSearchSeries(seriesId, ct);
            searched = true;
        }

        _logger.LogInformation(
            "Sonarr history {Id} for torrent {Hash} resolved (blocklisted, searched={Searched})",
            grabbed.Id, hash, searched);
        return new FailedDownloadResolution(true, true, searched, $"history {grabbed.Id}");
    }

    public void NotifySeriesUpdated(IReadOnlyList<TvSeries> series)
    {
        SeriesUpdated?.Invoke(this, new() { Series = series });
    }

    static TvSeries MapToTvSeries(SeriesDto s)
    {
        SeriesStatus seriesStatus = s.Status?.ToLowerInvariant() switch
        {
            "continuing" => SeriesStatus.Continuing,
            "ended" => SeriesStatus.Ended,
            "deleted" => SeriesStatus.Deleted,
            "upcoming" => SeriesStatus.Upcoming,
            _ => SeriesStatus.Continuing
        };

        string poster = ResolveImageUrl(s.Images, "poster");
        string banner = ResolveImageUrl(s.Images, "banner");
        string fanart = ResolveImageUrl(s.Images, "fanart");
        if (string.IsNullOrEmpty(fanart))
            fanart = ResolveImageUrl(s.Images, "background");

        List<TvSeason> seasons = (s.Seasons ?? []).Select(season => new TvSeason
        {
            SeasonNumber = season.SeasonNumber,
            Monitored = season.Monitored,
            EpisodeCount = season.Statistics?.EpisodeCount ?? 0,
            DownloadedEpisodeCount = season.Statistics?.EpisodeFileCount ?? 0,
            TotalEpisodeCount = season.Statistics?.TotalEpisodeCount ?? 0,
            SizeOnDisk = season.Statistics?.SizeOnDisk ?? 0
        }).ToList();

        int totalEpisodes = seasons.Sum(x => x.EpisodeCount);
        int downloadedEpisodes = seasons.Sum(x => x.DownloadedEpisodeCount);

        return new()
        {
            Id = s.Id,
            Title = s.Title,
            OriginalTitle = s.OriginalTitle,
            Year = s.Year,
            Overview = s.Overview,
            Network = s.Network,
            Genres = s.Genres ?? [],
            Rating = s.Ratings?.Value,
            RuntimeMinutes = s.Runtime,
            Status = seriesStatus,
            SeriesType = s.SeriesType,
            Monitored = s.Monitored,
            Added = s.Added,
            Seasons = seasons,
            PosterUrl = poster,
            BannerUrl = banner,
            FanartUrl = fanart,
            TvdbId = s.TvdbId,
            TmdbId = s.TmdbId,
            ImdbId = s.ImdbId ?? "",
            DownloadedEpisodes = downloadedEpisodes,
            TotalEpisodes = totalEpisodes,
            FirstAired = s.FirstAired,
            LastAired = s.LastAired,
            Certification = s.Certification ?? "",
            OriginalLanguage = s.OriginalLanguage?.Name ?? "",
            AirTime = s.AirTime ?? "",
            SeasonCount = s.Seasons?.Count,
            EpisodeFileCount = s.Statistics?.EpisodeFileCount,
            SizeOnDisk = s.Statistics?.SizeOnDisk
        };
    }
}
