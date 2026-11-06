using Arrk.Media.Abstractions;

namespace Arrk.Music.Abstractions;

/// <summary>
/// A music album owned by Arrk. <see cref="DeezerId"/> is the canonical identity (0 when the
/// album came from a download we couldn't match to Deezer, i.e. an "Unidentified" release).
/// </summary>
public class Album :
    IMediaDescriptor,
    IMediaArtwork,
    IMediaLibraryState,
    IDeezerIdentity
{
    public int Id { get; init; }
    public int ArtistId { get; init; }
    public string ArtistName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Overview { get; init; } = "";
    public string AlbumType { get; init; } = "";
    public List<string> SecondaryTypes { get; init; } = [];
    public DateTime? ReleaseDate { get; init; }
    public bool Monitored { get; init; }
    public bool AnyReleaseOk { get; init; }
    /// <summary>The Deezer album id captured when the album was added (0 when unknown).</summary>
    public long DeezerId { get; init; }
    /// <summary>The Deezer artist id for the album's artist (0 when unknown).</summary>
    public long ArtistDeezerId { get; init; }
    public int DurationSeconds { get; init; }
    public DateTime Added { get; init; }
    /// <summary>When the rescue last attempted to identify this album (null = never).</summary>
    public DateTime? IdentificationAttemptedAt { get; init; }
    /// <summary>When the re-tag pass last processed this album (null = never).</summary>
    public DateTime? RetagAttemptedAt { get; init; }
    public int TrackCount { get; init; }
    public int TrackFileCount { get; init; }
    public int TotalTrackCount { get; init; }
    public long SizeOnDisk { get; init; }
    public string PosterUrl { get; init; } = "";
    public string FanartUrl { get; init; } = "";
    public string BannerUrl { get; init; } = "";
    public string LogoUrl { get; init; } = "";
    public List<Track> Tracks { get; init; } = [];

    public bool HasFile => TrackFileCount > 0 && TotalTrackCount > 0 && TrackFileCount >= TotalTrackCount;

    public string Status => !Monitored ? "Not Monitored"
        : HasFile ? "Downloaded"
        : TrackFileCount > 0 ? "Partial"
        : "Missing";

    string IMediaDescriptor.OriginalTitle => Title;

    int IMediaDescriptor.Year => ReleaseDate?.Year ?? 0;

    IReadOnlyList<string> IMediaDescriptor.Genres => [];

    double? IMediaDescriptor.Rating => null;

    int? IMediaDescriptor.RuntimeMinutes => null;

    string IMediaDescriptor.Certification => "";

    string IMediaDescriptor.OriginalLanguage => "";

    DateTime IMediaLibraryState.AddedAt => Added;

    QualityInfo? IMediaLibraryState.Quality => null;

    MediaFileInfo? IMediaLibraryState.File => null;
}
