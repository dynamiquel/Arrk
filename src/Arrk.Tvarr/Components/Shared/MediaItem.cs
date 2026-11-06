using Arrk.Core.Models;
using Arrk.Core.Lifecycle;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Models;

namespace Arrk.Tvarr.Components.Shared;

public record QualityProfileInfo
{
    public int Id { get; init; }
    public string Name { get; init; } = "";

    public static QualityProfileInfo FromProfile(QualityProfile p) => new() { Id = p.Id, Name = p.Name };
}

public record SeasonStatusInfo
{
    public int SeasonNumber { get; init; }
    public bool IsMonitored { get; init; }
    public int DownloadedCount { get; init; }
    public int DownloadingCount { get; init; }
    public int TotalCount { get; init; }
    public bool IsDownloading { get; init; }

    public string Status => !IsMonitored ? "notmonitored"
        : IsDownloading ? "downloading"
        : DownloadedCount >= TotalCount && TotalCount > 0 ? "downloaded"
        : DownloadedCount > 0 ? "partial"
        : "missing";

    public string StatusLabel => Status switch
    {
        "downloaded" => "Downloaded",
        "downloading" => "Downloading",
        "partial" => "Partial",
        "missing" => "Missing",
        _ => "Not Monitored"
    };
}

public record MediaItem :
    IMediaDescriptor,
    IMediaArtwork,
    IMediaLibraryState,
    IMediaDownloadState,
    IMediaWatchState,
    IMediaRequestState
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string OriginalTitle { get; init; } = "";
    public string SeasonLabel { get; init; } = "";
    public int Year { get; init; }
    public DateTime? ReleaseDate { get; init; }
    public int EndYear { get; init; }
    public string Overview { get; init; } = "";
    public double? Rating { get; init; }
    public int? RuntimeMinutes { get; init; }
    public string PosterUrl { get; init; } = "";
    public string BackdropUrl { get; init; } = "";
    public string Certification { get; init; } = "";
    public List<string> Genres { get; init; } = [];
    public QualityInfo? Quality { get; init; }

    /// <summary>Detailed stream metadata (populated for items that have a file on disk).</summary>
    public MediaTechnicalInfo? Technical { get; init; }

    /// <summary>Compact "resolution · HDR" label for the card subline.</summary>
    public string FormatSummary => MediaFormatSummary.SummaryLine(Technical, Quality, includeAudio: false);

    // Library status
    public bool IsInLibrary { get; init; }
    public bool IsMonitored { get; init; }
    public bool HasFile { get; init; }
    public long SizeOnDisk { get; init; }
    public DateTime AddedAt { get; init; }
    public string LibraryStatus { get; init; } = "";

    /// <summary>
    /// True when the item is in the library without a file purely because its source has not
    /// been released on web or physical media yet (unreleased, announced, or still only in
    /// cinemas). Lets the card label a "Missing" item as "Not Released" instead.
    /// </summary>
    public bool NotYetReleased { get; init; }

    // Series-specific
    public int? SeasonCount { get; init; }
    public int DownloadedEpisodes { get; init; }
    public int TotalEpisodes { get; init; }
    public string SeriesStatus { get; init; } = "";
    public List<SeasonStatusInfo> SeasonStatuses { get; init; } = [];

    // Download state (from queue)
    public bool IsDownloading { get; init; }
    public long DownloadSize { get; init; }
    public long DownloadSizeLeft { get; init; }
    public string? DownloadStatus { get; init; }
    public string? DownloadTitle { get; init; }

    // Search result identifiers (for adding)
    public int TmdbId { get; init; }
    public int TvdbId { get; init; }
    public string ImdbId { get; init; } = "";
    public string YoutubeTrailerId { get; init; } = "";
    public bool IsSearchResult { get; init; }

    // Remote search result metadata
    public string? RemoteStatus { get; init; }
    public string? Network { get; init; }
    public List<int>? AvailableSeasonNumbers { get; init; }

    // Catalog metadata (TMDB)
    public string MediaType { get; init; } = "";
    public bool IsAnime { get; init; }
    public int VoteCount { get; init; }
    public double Popularity { get; init; }
    public string OriginalLanguage { get; init; } = "";

    /// <summary>Human friendly status for the library/remote distinction.</summary>
    public string LibraryBadge => IsInLibrary
        ? (HasFile ? "In Library" : "Tracked")
        : "Not in Library";

    // Request state: "" (none), "pending", "approved", "denied", "blacklisted".
    public string RequestState { get; init; } = "";

    public bool HasRequestState => !string.IsNullOrEmpty(RequestState);

    // Watch state (from the configured player).
    public double WatchProgress { get; init; }
    public bool IsWatched { get; init; }
    public long ResumeTicks { get; init; }
    public DateTime? LastPlayed { get; init; }

    public bool HasWatchProgress => WatchProgress > 0.001 || IsWatched;

    // Lifecycle / staleness (from the optional lifecycle module).
    public StaleLevel Staleness { get; init; } = StaleLevel.Fresh;
    public double? DaysRemaining { get; init; }
    public bool IsBookmarked { get; init; }
    public string? BookmarkLabel { get; init; }
    public bool IsPinned { get; init; }

    public bool HasStaleWarning => Staleness != StaleLevel.Fresh;

    public bool IsRemote => !IsInLibrary;

    string IMediaArtwork.FanartUrl => BackdropUrl;

    string IMediaArtwork.BannerUrl => "";

    string IMediaArtwork.LogoUrl => "";

    IReadOnlyList<string> IMediaDescriptor.Genres => Genres;

    bool IMediaLibraryState.Monitored => IsMonitored;

    QualityInfo? IMediaLibraryState.Quality => Quality;

    MediaFileInfo? IMediaLibraryState.File => null;

    public static MediaItem FromMovie(TvMovie movie)
    {
        int tmdbId = movie.TmdbId;
        return new()
        {
            Id = movie.Id,
            Title = movie.Title,
            Year = movie.Year,
            ReleaseDate = movie.ReleaseDate,
            Overview = movie.Overview,
            Rating = movie.Rating > 0 ? movie.Rating : null,
            RuntimeMinutes = movie.RuntimeMinutes,
            PosterUrl = movie.PosterUrl,
            Certification = movie.Certification,
            Genres = [.. movie.Genres],
            Quality = movie.Quality,
            Technical = movie.File?.Technical,
            IsInLibrary = true,
            IsMonitored = movie.Monitored,
            HasFile = movie.HasFile,
            SizeOnDisk = movie.SizeOnDisk,
            AddedAt = movie.Added,
            LibraryStatus = movie.HasFile ? "Downloaded" : "Missing",
            NotYetReleased = !movie.HasFile
                && ReleaseStatus.MovieNotYetReleased(movie.Status, movie.InCinemas, movie.DigitalRelease, movie.PhysicalRelease),
            MediaType = "movie",
            TmdbId = tmdbId,
            ImdbId = movie.ImdbId,
            YoutubeTrailerId = movie.YouTubeTrailerId,
        };
    }

    public static MediaItem FromSeries(TvSeries series)
    {
        int seasonCount = series.Seasons?.Count(s => s.SeasonNumber > 0) ?? 0;
        int downloaded = series.DownloadedEpisodes;
        int total = series.TotalEpisodes;

        int monitoredDownloaded = series.Seasons?
            .Where(s => s.SeasonNumber > 0 && s.Monitored)
            .Sum(s => s.DownloadedEpisodeCount) ?? 0;
        int monitoredTotal = series.Seasons?
            .Where(s => s.SeasonNumber > 0 && s.Monitored)
            .Sum(s => s.EpisodeCount) ?? 0;

        string status;
        if (total > 0 && downloaded >= total)
        {
            status = "Downloaded";
        }
        else if (monitoredTotal > 0 && monitoredDownloaded >= monitoredTotal)
        {
            status = "Downloaded Monitored";
        }
        else if (downloaded > 0)
        {
            status = "Partial";
        }
        else
        {
            status = "Missing";
        }

        List<SeasonStatusInfo> seasonStatuses = (series.Seasons ?? [])
            .Where(s => s.SeasonNumber > 0)
            .OrderBy(s => s.SeasonNumber)
            .Select(s => new SeasonStatusInfo
            {
                SeasonNumber = s.SeasonNumber,
                IsMonitored = s.Monitored,
                DownloadedCount = s.DownloadedEpisodeCount,
                TotalCount = s.EpisodeCount
            })
            .ToList();

        return new()
        {
            Id = series.Id,
            Title = series.Title,
            Year = series.Year,
            ReleaseDate = series.FirstAired,
            EndYear = series.Status == Arrk.Tv.Abstractions.SeriesStatus.Ended && series.LastAired.HasValue
                ? series.LastAired.Value.Year : 0,
            Overview = series.Overview,
            Rating = series.Rating > 0 ? series.Rating : null,
            RuntimeMinutes = series.RuntimeMinutes,
            PosterUrl = series.PosterUrl,
            Certification = series.Certification,
            Genres = [.. series.Genres],
            IsInLibrary = true,
            IsMonitored = series.Monitored,
            HasFile = downloaded > 0,
            SizeOnDisk = series.SizeOnDisk ?? 0,
            AddedAt = series.Added,
            LibraryStatus = status,
            NotYetReleased = downloaded == 0 && ReleaseStatus.SeriesNotYetReleased(series.Status, series.FirstAired),
            MediaType = "tv",
            SeasonCount = seasonCount,
            DownloadedEpisodes = downloaded,
            TotalEpisodes = total,
            SeriesStatus = series.Status.ToString(),
            SeasonStatuses = seasonStatuses,
            TmdbId = series.TmdbId,
            TvdbId = series.TvdbId,
            ImdbId = series.ImdbId,
            Network = series.Network,
        };
    }

    /// <summary>Fallback movie (Radarr lookup) mapping used when TMDB is not configured.</summary>
    public static MediaItem FromMovieLookup(MovieLookupDto lookup, bool isInLibrary)
    {
        return new()
        {
            Title = lookup.Title,
            Year = lookup.Year ?? 0,
            Overview = lookup.Overview,
            Rating = lookup.Ratings?.Value > 0 ? lookup.Ratings?.Value : null,
            RuntimeMinutes = lookup.Runtime,
            PosterUrl = lookup.Images?.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl ?? "",
            Certification = lookup.Certification,
            Genres = lookup.Genres,
            IsInLibrary = isInLibrary,
            IsSearchResult = !isInLibrary,
            MediaType = "movie",
            TmdbId = lookup.TmdbId,
            ImdbId = lookup.ImdbId,
            YoutubeTrailerId = lookup.YouTubeTrailerId,
            RemoteStatus = lookup.Status,
            NotYetReleased = !isInLibrary && ReleaseStatus.MovieNotYetReleased(RadarrStatus(lookup.Status), null, null, null),
        };
    }

    public static MediaItem FromSeriesLookup(SeriesLookupDto lookup, bool isInLibrary)
    {
        List<int> seasons = lookup.Seasons?
            .Where(s => s.SeasonNumber > 0)
            .Select(s => s.SeasonNumber)
            .ToList() ?? [];

        string status = lookup.Status?.ToLowerInvariant() switch
        {
            "ended" => "Ended",
            "continuing" => "Continuing",
            "upcoming" => "Upcoming",
            "deleted" => "Deleted",
            _ => ""
        };

        return new()
        {
            Title = lookup.Title,
            Year = lookup.Year ?? 0,
            Overview = lookup.Overview,
            Rating = lookup.Ratings?.Value > 0 ? lookup.Ratings?.Value : null,
            RuntimeMinutes = lookup.Runtime,
            PosterUrl = lookup.Images?.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl ?? "",
            BackdropUrl = lookup.Images?.FirstOrDefault(i => i.CoverType is "fanart" or "banner")?.RemoteUrl ?? "",
            Certification = lookup.Certification,
            Genres = lookup.Genres,
            IsInLibrary = isInLibrary,
            IsSearchResult = !isInLibrary,
            MediaType = "tv",
            TmdbId = lookup.TmdbId,
            TvdbId = lookup.TvdbId,
            ImdbId = lookup.ImdbId,
            RemoteStatus = lookup.Status,
            Network = lookup.Network,
            SeasonCount = seasons.Count,
            AvailableSeasonNumbers = seasons,
            SeriesStatus = status,
            NotYetReleased = !isInLibrary && ReleaseStatus.SeriesNotYetReleased(SonarrStatus(lookup.Status), null),
        };
    }

    /// <summary>Maps a Radarr lookup status string onto <see cref="MovieStatus"/>.</summary>
    static MovieStatus RadarrStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "announced" => MovieStatus.Announced,
        "incinemas" => MovieStatus.InCinemas,
        "tba" => MovieStatus.Tba,
        _ => MovieStatus.Released
    };

    /// <summary>Maps a Sonarr lookup status string onto <see cref="SeriesStatus"/>.</summary>
    static Arrk.Tv.Abstractions.SeriesStatus SonarrStatus(string? status) =>
        string.Equals(status, "upcoming", StringComparison.OrdinalIgnoreCase)
            ? Arrk.Tv.Abstractions.SeriesStatus.Upcoming
            : Arrk.Tv.Abstractions.SeriesStatus.Continuing;
}
