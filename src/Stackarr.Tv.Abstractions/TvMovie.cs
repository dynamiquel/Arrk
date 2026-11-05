using Stackarr.Media.Abstractions;

namespace Stackarr.Tv.Abstractions;

/// <summary>A movie in a TV library, normalised across the backend that owns it.</summary>
public class TvMovie :
    IMediaDescriptor,
    IMediaArtwork,
    IMediaLibraryState,
    ITmdbIdentity,
    IImdbIdentity
{
    public int Id { get; init; }

    public string Title { get; init; } = "";

    public string OriginalTitle { get; init; } = "";

    public int Year { get; init; }

    public string Overview { get; init; } = "";

    public IReadOnlyList<string> Genres { get; init; } = [];

    public double? Rating { get; init; }

    public int? RuntimeMinutes { get; init; }

    public MovieStatus Status { get; init; }

    public bool Monitored { get; init; }

    public bool HasFile { get; init; }

    public DateTime Added { get; init; }

    public QualityInfo Quality { get; init; } = new();

    public MediaFileInfo? File { get; init; }

    public string PosterUrl { get; init; } = "";

    public string FanartUrl { get; init; } = "";

    public string BannerUrl { get; init; } = "";

    public string LogoUrl { get; init; } = "";

    public int TmdbId { get; init; }

    public string ImdbId { get; init; } = "";

    public ReleaseSource ReleaseSource { get; init; }

    public string Certification { get; init; } = "";

    public string OriginalLanguage { get; init; } = "";

    public DateTime? InCinemas { get; init; }

    public DateTime? PhysicalRelease { get; init; }

    public DateTime? DigitalRelease { get; init; }

    public DateTime? ReleaseDate { get; init; }

    public string Studio { get; init; } = "";

    public string YouTubeTrailerId { get; init; } = "";

    public string Website { get; init; } = "";

    public string CollectionTitle { get; init; } = "";

    public long SizeOnDisk { get; init; }

    DateTime IMediaLibraryState.AddedAt => Added;

    QualityInfo? IMediaLibraryState.Quality => Quality;
}

public enum MovieStatus
{
    Released,
    Announced,
    InCinemas,
    Deleted,
    Tba
}

public enum ReleaseSource
{
    Disc,
    Web,
    Unknown
}
