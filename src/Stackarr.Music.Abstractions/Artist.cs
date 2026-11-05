using Stackarr.Media.Abstractions;

namespace Stackarr.Music.Abstractions;

public enum ArtistStatus
{
    Continuing,
    Ended,
    Deleted
}

/// <summary>
/// A music artist owned by Stackarr. <see cref="DeezerId"/> is the canonical identity used to link
/// Stackarr requests to the library (0 when unknown).
/// </summary>
public class Artist :
    IMediaDescriptor,
    IMediaArtwork,
    IMediaLibraryState,
    IDeezerIdentity
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string SortName { get; init; } = "";
    public string Overview { get; init; } = "";
    public List<string> Genres { get; init; } = [];
    public double? Rating { get; init; }
    public ArtistStatus Status { get; init; }
    public string ArtistType { get; init; } = "";
    public bool Monitored { get; init; }
    public DateTime Added { get; init; }
    /// <summary>The Deezer artist id (0 when unknown).</summary>
    public long DeezerId { get; init; }
    public int QualityProfileId { get; init; }
    public int MetadataProfileId { get; init; }
    public string Path { get; init; } = "";
    public string PosterUrl { get; init; } = "";
    public string FanartUrl { get; init; } = "";
    public string BannerUrl { get; init; } = "";
    public string LogoUrl { get; init; } = "";
    public int AlbumCount { get; init; }
    public int TrackFileCount { get; init; }
    public int TrackCount { get; init; }
    public int TotalTrackCount { get; init; }
    public long SizeOnDisk { get; init; }
    public List<Album> Albums { get; init; } = [];

    public bool HasAnyTracks => TrackFileCount > 0;

    string IMediaDescriptor.Title => Name;

    string IMediaDescriptor.OriginalTitle => Name;

    int IMediaDescriptor.Year => 0;

    DateTime? IMediaDescriptor.ReleaseDate => null;

    IReadOnlyList<string> IMediaDescriptor.Genres => Genres;

    int? IMediaDescriptor.RuntimeMinutes => null;

    string IMediaDescriptor.Certification => "";

    string IMediaDescriptor.OriginalLanguage => "";

    bool IMediaLibraryState.HasFile => HasAnyTracks;

    DateTime IMediaLibraryState.AddedAt => Added;

    QualityInfo? IMediaLibraryState.Quality => null;

    MediaFileInfo? IMediaLibraryState.File => null;
}
