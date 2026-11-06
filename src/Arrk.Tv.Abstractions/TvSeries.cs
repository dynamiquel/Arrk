using Arrk.Media.Abstractions;

namespace Arrk.Tv.Abstractions;

/// <summary>A TV series in a library, with its seasons.</summary>
public class TvSeries :
    IMediaDescriptor,
    IMediaArtwork,
    IMediaLibraryState,
    ITmdbIdentity,
    ITvdbIdentity,
    IImdbIdentity
{
    public int Id { get; init; }

    public string Title { get; init; } = "";

    public string OriginalTitle { get; init; } = "";

    public int Year { get; init; }

    public string Overview { get; init; } = "";

    public string Network { get; init; } = "";

    public IReadOnlyList<string> Genres { get; init; } = [];

    public double? Rating { get; init; }

    public int? RuntimeMinutes { get; init; }

    public SeriesStatus Status { get; init; }

    public string SeriesType { get; init; } = "standard";

    public bool Monitored { get; init; }

    public DateTime Added { get; init; }

    public IReadOnlyList<TvSeason> Seasons { get; init; } = [];

    public string PosterUrl { get; init; } = "";

    public string BannerUrl { get; init; } = "";

    public string FanartUrl { get; init; } = "";

    public string LogoUrl { get; init; } = "";

    public int TvdbId { get; init; }

    public int TmdbId { get; init; }

    public string ImdbId { get; init; } = "";

    public int DownloadedEpisodes { get; init; }

    public int TotalEpisodes { get; init; }

    public DateTime? FirstAired { get; init; }

    public DateTime? ReleaseDate => FirstAired;

    public DateTime? LastAired { get; init; }

    public string Certification { get; init; } = "";

    public string OriginalLanguage { get; init; } = "";

    public string AirTime { get; init; } = "";

    public int? SeasonCount { get; init; }

    public int? EpisodeFileCount { get; init; }

    public long? SizeOnDisk { get; init; }

    long IMediaLibraryState.SizeOnDisk => SizeOnDisk ?? 0;

    bool IMediaLibraryState.HasFile => DownloadedEpisodes > 0 || SizeOnDisk > 0;

    DateTime IMediaLibraryState.AddedAt => Added;

    QualityInfo? IMediaLibraryState.Quality => null;

    MediaFileInfo? IMediaLibraryState.File => null;
}

public enum SeriesStatus
{
    Continuing,
    Ended,
    Deleted,
    Upcoming
}

/// <summary>One season in a series.</summary>
public class TvSeason : IMediaLibraryState
{
    public int SeasonNumber { get; init; }

    public bool Monitored { get; init; }

    public int EpisodeCount { get; init; }

    public int DownloadedEpisodeCount { get; init; }

    public int TotalEpisodeCount { get; init; }

    public long SizeOnDisk { get; init; }

    public QualityInfo? Quality { get; init; }

    bool IMediaLibraryState.HasFile => DownloadedEpisodeCount > 0;

    DateTime IMediaLibraryState.AddedAt => default;

    MediaFileInfo? IMediaLibraryState.File => null;
}

/// <summary>One episode in a series.</summary>
public class TvEpisode : IMediaDescriptor, IMediaLibraryState
{
    public int Id { get; init; }

    public int SeriesId { get; init; }

    /// <summary>The backend's episode-file id, used to delete the file for one episode.</summary>
    public int EpisodeFileId { get; init; }

    public int SeasonNumber { get; init; }

    public int EpisodeNumber { get; init; }

    public string Title { get; init; } = "";

    public string Overview { get; init; } = "";

    public bool Monitored { get; init; }

    public bool HasFile { get; init; }

    public DateTime? AirDate { get; init; }

    public DateTime? ReleaseDate => AirDate;

    public string ImageUrl { get; init; } = "";

    public QualityInfo? Quality { get; init; }

    public MediaFileInfo? File { get; init; }

    string IMediaDescriptor.OriginalTitle => Title;

    int IMediaDescriptor.Year => AirDate?.Year ?? 0;

    IReadOnlyList<string> IMediaDescriptor.Genres => [];

    double? IMediaDescriptor.Rating => null;

    int? IMediaDescriptor.RuntimeMinutes => null;

    string IMediaDescriptor.Certification => "";

    string IMediaDescriptor.OriginalLanguage => "";

    long IMediaLibraryState.SizeOnDisk => File?.SizeBytes ?? 0;

    DateTime IMediaLibraryState.AddedAt => File?.DateAdded ?? default;
}
