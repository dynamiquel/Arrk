using Arrk.Core.Requests;
using Microsoft.Extensions.Logging;
using Arrk.Core.Enums;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Notifications;
using Arrk.Core.Playback;
using Arrk.Core.Services;
using Arrk.Media.Abstractions;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Models;

namespace Arrk.Tvarr.Services;

public partial class RadarrService : ArrService, IMovieLibrary, IMovieLibraryWriter
{
    readonly RadarrClient _client;
    readonly ILogger<RadarrService> _logger;
    readonly IEventStore? _eventStore;
    readonly IEventBus? _eventBus;
    readonly ITorrentDownloader? _torrents;
    protected override ArrClient Client => _client;

    public event EventHandler<MoviesChangedEventArgs>? MoviesUpdated;

    public RadarrService(
        RadarrClient client,
        ILogger<RadarrService> logger,
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

    public async Task<List<TvMovie>> GetAllMovies(CancellationToken ct = default)
    {
        List<MovieDto> movies = await _client.GetMovies(ct);
        return movies.Select(MapToTvMovie).ToList();
    }

    public async Task<TvMovie?> GetMovie(int id, CancellationToken ct = default)
    {
        MovieDto? movie = await _client.GetMovie(id, ct);
        return movie == null ? null : MapToTvMovie(movie);
    }

    public async Task<List<MovieLookupDto>> SearchMovie(string query, CancellationToken ct = default)
    {
        return await _client.SearchMovie(query, ct);
    }

    public async Task DeleteMovie(int id, bool deleteFiles = false, CancellationToken ct = default)
    {
        TvMovie? movie = null;
        try { movie = await GetMovie(id, ct); } catch { /* best effort for the removed event */ }

        // Radarr's delete endpoint never touches the download client, so an in-progress (or
        // still-seeding) torrent would keep running after the movie is gone. Delete the movie's
        // downloads from Transmission first, using the queue (in-progress) and history (finished)
        // to map them to torrent hashes.
        await RemoveMediaDownloadsAsync(id, deleteFiles, ct);

        await _client.DeleteMovie(id, deleteFiles, ct);
        _logger.LogInformation("Deleted movie {Id}, deleteFiles={DeleteFiles}", id, deleteFiles);

        if (movie is not null && _eventBus is not null)
        {
            Dictionary<string, string> providerIds = ProviderIdMap.Build(
                (TvProviderIds.Tmdb, movie.TmdbId > 0 ? movie.TmdbId.ToString() : null),
                (TvProviderIds.Imdb, movie.ImdbId));
            await _eventBus.Publish(new MediaRemovedEvent(TvMediaDomains.Movie, providerIds, movie.Title), ct);
        }
    }

    async Task RemoveMediaDownloadsAsync(int movieId, bool deleteFiles, CancellationToken ct)
    {
        if (_torrents is null)
            return;

        HashSet<string> hashes = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (RadarrQueueItemDto item in await _client.GetQueue(ct, pageSize: 1000))
            {
                if (item.MovieId == movieId && !string.IsNullOrWhiteSpace(item.DownloadId))
                    hashes.Add(item.DownloadId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Radarr queue for movie {MovieId}", movieId);
        }

        try
        {
            foreach (HistoryDto history in await _client.GetMovieHistory(movieId, ct))
            {
                if (!string.IsNullOrWhiteSpace(history.DownloadId))
                    hashes.Add(history.DownloadId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Radarr history for movie {MovieId}", movieId);
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
            _logger.LogWarning(ex, "Could not list torrents to clean up for deleted movie");
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

    public override async Task<string> GetDefaultRootFolder(CancellationToken ct = default)
    {
        List<RootFolder> folders = await _client.GetRootFolders(ct);
        return folders.FirstOrDefault()?.Path ?? "/movies";
    }

    public async Task SearchAndAddMovie(int tmdbId, int? qualityProfileId = null, bool monitored = true, bool searchForMovie = true, CancellationToken ct = default)
    {
        int profileId = qualityProfileId ?? await GetDefaultQualityProfileId(ct);
        string rootFolder = await GetDefaultRootFolder(ct);

        MovieCreateRequest movie = new MovieCreateRequest
        {
            TmdbId = tmdbId,
            Title = "",
            QualityProfileId = profileId,
            Path = $"{rootFolder}/{tmdbId}",
            Monitored = monitored,
            MinimumAvailability = "released"
        };
        MovieDto created = await _client.CreateMovie(movie, ct);
        _logger.LogInformation("Added movie with TMDb ID {TmdbId}", tmdbId);

        await RecordMediaAddedAsync("Movie", created.Title, created.Year, created.TmdbId, created.Id, created.Images, ct);

        if (searchForMovie && created.Id > 0)
        {
            try
            {
                await _client.CommandSearchMovie(created.Id, ct);
                _logger.LogInformation("Triggered initial search for movie {Id}", created.Id);
            }
            catch (Exception ex)
            {
                // The movie is still added; a failed initial search should not fail the add.
                _logger.LogWarning(ex, "Initial search for movie {Id} failed", created.Id);
            }
        }
    }

    /// <summary>
    /// Adds a movie without starting an indexer search. Local imports call this and then hand the
    /// uploaded file to Radarr's manual-import command, so a search would only race the file.
    /// </summary>
    public async Task<MovieDto> AddMovieForImportAsync(
        int tmdbId,
        int? qualityProfileId = null,
        CancellationToken ct = default)
    {
        int profileId = qualityProfileId is > 0 ? qualityProfileId.Value : await GetDefaultQualityProfileId(ct);
        string rootFolder = await GetDefaultRootFolder(ct);

        MovieCreateRequest movie = new()
        {
            TmdbId = tmdbId,
            Title = "",
            QualityProfileId = profileId,
            Path = $"{rootFolder}/{tmdbId}",
            Monitored = true,
            MinimumAvailability = "released"
        };

        MovieDto created = await _client.CreateMovie(movie, ct);
        _logger.LogInformation("Added movie with TMDb ID {TmdbId} for local import", tmdbId);
        await RecordMediaAddedAsync("Movie", created.Title, created.Year, created.TmdbId, created.Id, created.Images, ct);
        return created;
    }

    protected override async Task ToggleMonitoredCore(int id, bool monitored, CancellationToken ct)
    {
        MovieDto? movie = await _client.GetMovie(id, ct);
        if (movie == null)
            return;
        movie.Monitored = monitored;
        await _client.UpdateMovie(movie, ct);
        _logger.LogInformation("Movie {Id} monitored={Monitored}", id, monitored);
    }

    public async Task<List<HistoryDto>> GetMovieHistory(int movieId, CancellationToken ct = default)
    {
        return await _client.GetMovieHistory(movieId, ct);
    }

    /// <summary>Recent Radarr history, newest first (used by the media import monitor).</summary>
    public async Task<List<HistoryDto>> GetRecentHistory(int pageSize = 200, CancellationToken ct = default)
    {
        return await _client.GetRecentHistory(pageSize, ct);
    }

    /// <summary>
    /// Switches a movie to another quality profile (the assistant's "upgrade to 4K" action) and asks
    /// Radarr to search for a better release. Radarr keeps the existing file until the upgrade
    /// imports, so this never loses the current copy.
    /// </summary>
    public async Task SetQualityProfile(int movieId, int qualityProfileId, CancellationToken ct = default)
    {
        MovieDto? movie = await _client.GetMovie(movieId, ct);
        if (movie is null)
        {
            _logger.LogWarning("Movie {Id} not found for quality profile update", movieId);
            return;
        }

        movie.QualityProfileId = qualityProfileId;
        await _client.UpdateMovie(movie, ct);
        _logger.LogInformation("Movie {Id} quality profile set to {Profile}", movieId, qualityProfileId);
    }

    public async Task CommandSearchMovie(int movieId, CancellationToken ct = default)
    {
        await _client.CommandSearchMovie(movieId, ct);
        _logger.LogInformation("Triggered search for movie {MovieId}", movieId);
    }

    /// <summary>
    /// Queues a search for every monitored movie without a file. Used by the hourly missing-content
    /// job so a movie that failed or was removed earlier is retried automatically.
    /// </summary>
    /// <returns>The number of movies queued for search.</returns>
    public async Task SearchMissingAsync(CancellationToken ct = default)
    {
        await _client.CommandSearchMissingMovies(ct);
        _logger.LogInformation("Triggered Radarr missing-movie search");
    }

    /// <summary>Release candidates for a movie's interactive search.</summary>
    public async Task<List<ReleaseResourceDto>> GetReleases(int movieId, CancellationToken ct = default)
    {
        return await _client.GetReleases(movieId, ct);
    }

    /// <summary>Grabs a chosen release through Radarr's download client.</summary>
    public async Task GrabRelease(string guid, int indexerId, CancellationToken ct = default)
    {
        await _client.GrabRelease(guid, indexerId, ct);
        _logger.LogInformation("Grabbed release {Guid} from Radarr indexer {IndexerId}", guid, indexerId);
    }

    public async Task<List<RadarrQueueItemDto>> GetQueue(CancellationToken ct = default)
    {
        return await _client.GetQueue(ct);
    }

    async Task RecordMediaAddedAsync(
        string mediaType, string title, int year, int providerId, int internalId, List<ImageDto>? images, CancellationToken ct)
    {
        if (_eventStore is null)
            return;

        string display = string.IsNullOrWhiteSpace(title) ? providerId.ToString() : title;
        string body = $"{display} ({year}) was added to the library";
        MediaEntityRef? entity = providerId > 0
            ? new MediaEntityRef(
                "tv",
                TvMediaDomains.MovieDomain,
                MediaItemKeys.Movie(providerId),
                display,
                year > 0 ? year.ToString() : null,
                Url: "/movies")
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
                    Title = $"{mediaType} added",
                    Body = body,
                    Severity = EventSeverity.Info,
                    Source = providerId.ToString(),
                    SourceName = display,
                    Timestamp = DateTime.UtcNow,
                    Style = MediaArtStyle(images),
                    Fields =
                    [
                        new("Type", mediaType),
                        new("Year", year.ToString()),
                        new("Monitored", "true")
                    ],
                    Audience = NotificationAudience.Everyone with { FollowersOf = entity }
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

    public async Task DeleteQueueItem(int queueItemId, bool blocklist = false, CancellationToken ct = default)
    {
        await _client.DeleteQueueItem(queueItemId, blocklist, removeFromClient: true, searchAgain: false, ct);
        _logger.LogInformation("Deleted queue item {Id}, blocklist={Blocklist}", queueItemId, blocklist);
    }

    public async Task DeleteQueueItemByHash(string hash, bool blocklist = true, CancellationToken ct = default)
    {
        List<RadarrQueueItemDto> queue = await _client.GetQueue(ct, pageSize: 1000);
        RadarrQueueItemDto? item = queue.FirstOrDefault(q => q.DownloadId.Equals(hash, StringComparison.OrdinalIgnoreCase));
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
    /// Auto-resolves a removed torrent: traces it back to its movie, blocklists the failed release
    /// and triggers a fresh search. Prefers the live queue (which still knows the movie); falls back
    /// to history, which persists after the download has been removed from the client.
    /// </summary>
    public async Task<FailedDownloadResolution> ResolveFailedDownload(string hash, CancellationToken ct = default)
    {
        List<RadarrQueueItemDto> queue = await _client.GetQueue(ct, pageSize: 1000);
        RadarrQueueItemDto? item = queue.FirstOrDefault(
            q => q.DownloadId.Equals(hash, StringComparison.OrdinalIgnoreCase));

        if (item is not null)
        {
            await _client.DeleteQueueItem(item.Id, blocklist: true, removeFromClient: false, searchAgain: true, ct);
            _logger.LogInformation(
                "Radarr queue item {Id} for torrent {Hash} removed, blocklisted and re-searching", item.Id, hash);
            return new FailedDownloadResolution(true, true, true, $"queue item {item.Id}");
        }

        List<HistoryDto> history = await _client.GetHistoryByDownloadId(hash, ct);
        HistoryDto? grabbed = history
            .Where(h => h.EventType is "grabbed" or "downloadFailed")
            .OrderByDescending(h => h.Date)
            .FirstOrDefault();
        if (grabbed is null)
        {
            _logger.LogWarning("No Radarr queue item or grabbable history found for torrent {Hash}", hash);
            return new FailedDownloadResolution(false, false, false, "no queue item or history");
        }

        // Only the first failure needs marking; the release is already blocklisted otherwise.
        if (grabbed.EventType == "grabbed")
            await _client.MarkHistoryFailed(grabbed.Id, ct);

        bool searched = false;
        if (grabbed.MovieId is int movieId and > 0)
        {
            await _client.CommandSearchMovie(movieId, ct);
            searched = true;
        }

        _logger.LogInformation(
            "Radarr history {Id} for torrent {Hash} resolved (blocklisted, searched={Searched})",
            grabbed.Id, hash, searched);
        return new FailedDownloadResolution(true, true, searched, $"history {grabbed.Id}");
    }

    public async Task DeleteMovieFile(int movieFileId, CancellationToken ct = default)
    {
        await _client.DeleteMovieFile(movieFileId, ct);
        _logger.LogInformation("Deleted movie file {Id}", movieFileId);
    }

    public async Task<List<MovieFileDto>> GetMovieFiles(int movieId, CancellationToken ct = default)
    {
        return await _client.GetMovieFiles(movieId, ct);
    }

    public void NotifyMoviesUpdated(IReadOnlyList<TvMovie> movies)
    {
        MoviesUpdated?.Invoke(this, new() { Movies = movies });
    }

    static TvMovie MapToTvMovie(MovieDto m)
    {
        MovieStatus movieStatus = m.Status?.ToLowerInvariant() switch
        {
            "released" => MovieStatus.Released,
            "announced" => MovieStatus.Announced,
            "incinemas" => MovieStatus.InCinemas,
            "tba" => MovieStatus.Tba,
            _ => MovieStatus.Released
        };

        string poster = ResolveImageUrl(m.Images, "poster");
        string fanart = ResolveImageUrl(m.Images, "fanart");
        if (string.IsNullOrEmpty(fanart))
            fanart = ResolveImageUrl(m.Images, "background");

        QualityInfo quality = new();
        MediaFileInfo? file = null;

        MovieFileDto? movieFile = m.MovieFile ?? (m.MovieFiles.Count > 0 ? m.MovieFiles[0] : null);
        if (movieFile != null)
        {
            MediaInfoDto? mi = movieFile.MediaInfo;
            quality = MediaInfoMapper.QualityFrom(movieFile.Quality, mi);

            ReleaseSource source = DetermineReleaseSource(quality);

            file = new()
            {
                Path = movieFile.Path,
                SizeBytes = movieFile.Size,
                DateAdded = movieFile.DateAdded,
                Quality = quality,
                Technical = MediaInfoMapper.TechnicalFrom(mi, movieFile.Path, movieFile.Size, movieFile.DateAdded)
            };
        }

        return new()
        {
            Id = m.Id,
            Title = m.Title,
            OriginalTitle = m.OriginalTitle,
            Year = m.Year,
            Overview = m.Overview,
            Genres = m.Genres ?? [],
            Rating = m.Ratings?.Value,
            RuntimeMinutes = m.Runtime,
            Status = movieStatus,
            Monitored = m.Monitored,
            HasFile = m.HasFile,
            Added = m.Added,
            Quality = quality,
            File = file,
            PosterUrl = poster,
            FanartUrl = fanart,
            TmdbId = m.TmdbId,
            ImdbId = m.ImdbId ?? "",
            ReleaseSource = file != null ? DetermineReleaseSource(quality) : ReleaseSource.Unknown,
            Certification = m.Certification ?? "",
            OriginalLanguage = m.OriginalLanguage?.Name ?? "",
            InCinemas = m.InCinemas,
            PhysicalRelease = m.PhysicalRelease,
            DigitalRelease = m.DigitalRelease,
            ReleaseDate = m.ReleaseDate,
            Studio = m.Studio ?? "",
            YouTubeTrailerId = m.YouTubeTrailerId ?? "",
            Website = m.Website ?? "",
            CollectionTitle = m.Collection?.Title ?? "",
            SizeOnDisk = m.SizeOnDisk
        };
    }

    static ReleaseSource DetermineReleaseSource(QualityInfo quality)
    {
        return quality.Source switch
        {
            QualitySource.Bluray or QualitySource.Remux => ReleaseSource.Disc,
            QualitySource.Web => ReleaseSource.Web,
            _ => ReleaseSource.Unknown
        };
    }
}
