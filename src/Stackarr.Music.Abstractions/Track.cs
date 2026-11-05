using Stackarr.Media.Abstractions;

namespace Stackarr.Music.Abstractions;

/// <summary>A single music track owned by Stackarr.</summary>
public class Track : IMediaDescriptor, IMediaLibraryState, IDeezerIdentity
{
    public int Id { get; init; }
    public int AlbumId { get; init; }
    public int ArtistId { get; init; }
    public string Title { get; init; } = "";
    public int TrackNumber { get; init; }
    public int AbsoluteTrackNumber { get; init; }
    public int MediumNumber { get; init; } = 1;
    public int DurationSeconds { get; init; }
    public bool Monitored { get; init; }
    public bool HasFile { get; init; }
    public bool Explicit { get; init; }
    /// <summary>Deezer track id captured when the album was added (0 when we never matched it).</summary>
    public long DeezerId { get; init; }
    /// <summary>Placed file path relative to the library root (empty when there is no file).</summary>
    public string FilePath { get; init; } = "";
    public DateTime? ReleaseDate { get; init; }

    /// <summary>Where this track's lyrics lookup stands (None until the lyrics sweep looks at it).</summary>
    public LyricsState LyricsState { get; init; }

    /// <summary>The sync level of the lyrics we hold for this track.</summary>
    public LyricsTier LyricsTier { get; init; }

    /// <summary>True when the track was never matched to Deezer, e.g. a non-official release found
    /// only on Soulseek.</summary>
    public bool IsUnidentified => DeezerId <= 0;

    string IMediaDescriptor.OriginalTitle => Title;

    int IMediaDescriptor.Year => ReleaseDate?.Year ?? 0;

    string IMediaDescriptor.Overview => "";

    IReadOnlyList<string> IMediaDescriptor.Genres => [];

    double? IMediaDescriptor.Rating => null;

    int? IMediaDescriptor.RuntimeMinutes => null;

    string IMediaDescriptor.Certification => "";

    string IMediaDescriptor.OriginalLanguage => "";

    long IMediaLibraryState.SizeOnDisk => 0;

    DateTime IMediaLibraryState.AddedAt => ReleaseDate ?? default;

    QualityInfo? IMediaLibraryState.Quality => null;

    MediaFileInfo? IMediaLibraryState.File => null;
}
