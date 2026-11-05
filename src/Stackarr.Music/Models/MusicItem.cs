using Stackarr.Music.Services;
using Stackarr.Core.Lifecycle;
using Stackarr.Core.Models;

namespace Stackarr.Music.Models;

/// <summary>The kind of music entity a <see cref="MusicItem"/> represents.</summary>
public enum MusicEntityType
{
    Artist,
    Album,
    Track,
    /// <summary>A curated/editorial playlist from the discovery provider (Deezer), not a library playlist.</summary>
    Playlist
}

public record MusicTrackItem
{
    public int Number { get; init; }
    public int DiscNumber { get; init; } = 1;
    public string Title { get; init; } = "";
    public int DurationSeconds { get; init; }
    public long DeezerId { get; init; }
    /// <summary>Library track id (0 for a tracklist that isn't in our library yet).</summary>
    public int TrackId { get; init; }
    /// <summary>Placed file path relative to the library root (empty when not downloaded).</summary>
    public string FilePath { get; init; } = "";
    public string PreviewUrl { get; init; } = "";
    public bool HasFile { get; init; }

    /// <summary>Where this track's lyrics lookup stands (None when we've never looked).</summary>
    public LyricsState LyricsState { get; init; }

    /// <summary>How well synced the track's lyrics are.</summary>
    public LyricsTier LyricsTier { get; init; }

    /// <summary>True when lyrics are on disk for this song, so the menu offers to read them.</summary>
    public bool HasLyrics => LyricsState == LyricsState.Found;

    /// <summary>True when the track has no Deezer match, the only tracks that should be
    /// unidentified are ones never found on Deezer (non-official songs).</summary>
    public bool IsUnidentified { get; init; }

    /// <summary>
    /// True when Stackarr wants this track, through an explicit album or song request. The rest of an
    /// album we grabbed one song from, or a song a playlist grabbed, is unmonitored, so it reads as
    /// Unmonitored rather than Missing, like an unmonitored episode.
    /// </summary>
    public bool Monitored { get; init; } = true;

    // Per-track download state (an in-flight grab), so an album/playlist track list can show what
    // is still coming down and how far along it is.
    public bool IsDownloading { get; init; }
    public long DownloadSize { get; init; }
    public long DownloadSizeLeft { get; init; }

    public double DownloadPercent => DownloadSize > 0
        ? Math.Clamp((DownloadSize - DownloadSizeLeft) / (double)DownloadSize * 100, 0, 100)
        : 0;

    /// <summary>"Downloaded" | "Downloading" | "Missing" (monitored) | "Unmonitored" (not wanted).</summary>
    public string LibraryStatus => IsDownloading ? "Downloading"
        : HasFile ? "Downloaded"
        : Monitored ? "Missing"
        : "Unmonitored";

    public string DurationLabel => DurationSeconds <= 0
        ? ""
        : TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}

/// <summary>
/// A music entity (artist, album or track) from the discovery layer or the library. Kept separate
/// from the movie/TV <see cref="MediaItem"/> because music is a distinct hierarchy
/// (artist → album → track) and uses square artwork.
/// </summary>
public record MusicItem :
    IMediaDescriptor,
    IMediaArtwork,
    IMediaLibraryState,
    IMediaDownloadState,
    IMediaRequestState
{
    public MusicEntityType EntityType { get; init; } = MusicEntityType.Artist;
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string ArtistName { get; init; } = "";
    public string AlbumName { get; init; } = "";
    public long DeezerId { get; init; }
    public long ArtistDeezerId { get; init; }
    public long AlbumDeezerId { get; init; }
    public string PreviewUrl { get; init; } = "";
    public int Year { get; init; }
    public DateTime? ReleaseDate { get; init; }
    public string Overview { get; init; } = "";
    public List<string> Genres { get; init; } = [];
    public double? Rating { get; init; }
    public int? Listeners { get; init; }
    public string ArtUrl { get; init; } = "";
    public string BackdropUrl { get; init; } = "";
    public string AlbumType { get; init; } = "";
    public string ReleaseStatus { get; init; } = "";
    public List<MusicTrackItem> Tracks { get; init; } = [];
    public int TrackCount { get; init; }
    public int AlbumCount { get; init; }
    public int TrackFileCount { get; init; }
    public long SizeOnDisk { get; init; }
    public DateTime AddedAt { get; init; }

    // Library state (resolved against our own library + Navidrome).
    public bool IsInLibrary { get; init; }
    public bool IsMonitored { get; init; }
    public bool HasFile { get; init; }
    public int LibraryId { get; init; }

    /// <summary>True when the entity was never matched to Deezer (only possible for tracks/albums
    /// that simply don't exist on Deezer, e.g. non-official Soulseek releases).</summary>
    public bool IsUnidentified { get; init; }

    // Request state: "" | "pending" | "approved" | "denied" | "blacklisted".
    public string RequestState { get; init; } = "";

    // Download / library status (mirrors the movie/TV cards).
    public bool IsDownloading { get; init; }
    public long DownloadSize { get; init; }
    public long DownloadSizeLeft { get; init; }
    public string DownloadStatus { get; init; } = "";
    public string LibraryStatus { get; init; } = "";

    /// <summary>Short format badge (e.g. "Lossless", "Dolby Atmos", "DTS:X") for cards.</summary>
    public string FormatLabel { get; init; } = "";

    /// <summary>Detailed audio technical info (library albums only).</summary>
    public MusicTechnical? Technical { get; init; }

    // Lifecycle / staleness (from the optional lifecycle module).
    public StaleLevel Staleness { get; init; } = StaleLevel.Fresh;
    public double? DaysRemaining { get; init; }
    public bool IsBookmarked { get; init; }
    public string? BookmarkLabel { get; init; }
    public bool IsPinned { get; init; }

    public bool HasStaleWarning => Staleness != StaleLevel.Fresh;

    public double DownloadPercent => DownloadSize > 0
        ? Math.Clamp((DownloadSize - DownloadSizeLeft) / (double)DownloadSize * 100, 0, 100)
        : 0;

    public string StatusVariant => LibraryStatus switch
    {
        "Downloaded" => "success",
        "Partial" => "warning",
        "Missing" => "error",
        "Unmonitored" => "neutral",
        "Downloading" => "info",
        "Tracked" => "info",
        _ => "neutral"
    };

    public bool HasRequestState => !string.IsNullOrEmpty(RequestState);
    public int ReleaseYear => Year > 0 ? Year : 0;
    public string Subtitle => EntityType == MusicEntityType.Artist ? "Artist" : ArtistName;

    public string LibraryBadge => IsInLibrary
        ? (HasFile ? "In Library" : "Tracked")
        : "Not in Library";

    string IMediaDescriptor.OriginalTitle => Title;

    int? IMediaDescriptor.RuntimeMinutes => null;

    string IMediaDescriptor.Certification => "";

    string IMediaDescriptor.OriginalLanguage => "";

    IReadOnlyList<string> IMediaDescriptor.Genres => Genres;

    string IMediaArtwork.PosterUrl => ArtUrl;

    string IMediaArtwork.FanartUrl => BackdropUrl;

    string IMediaArtwork.BannerUrl => "";

    string IMediaArtwork.LogoUrl => "";

    bool IMediaLibraryState.Monitored => IsMonitored;

    QualityInfo? IMediaLibraryState.Quality => null;

    MediaFileInfo? IMediaLibraryState.File => null;

    string? IMediaDownloadState.DownloadTitle => null;

    public static MusicItem FromArtist(Artist artist) => new()
    {
        EntityType = MusicEntityType.Artist,
        Id = artist.Id,
        LibraryId = artist.Id,
        Title = artist.Name,
        ArtistName = artist.Name,
        DeezerId = artist.DeezerId,
        ArtistDeezerId = artist.DeezerId,
        Overview = artist.Overview,
        Genres = artist.Genres,
        Rating = artist.Rating,
        ArtUrl = artist.PosterUrl,
        BackdropUrl = artist.FanartUrl,
        IsInLibrary = true,
        IsMonitored = artist.Monitored,
        HasFile = artist.TrackFileCount > 0,
        TrackCount = artist.TrackCount,
        TrackFileCount = artist.TrackFileCount,
        SizeOnDisk = artist.SizeOnDisk,
        AddedAt = artist.Added
    };

    public static MusicItem FromAlbum(Album album) => new()
    {
        EntityType = MusicEntityType.Album,
        Id = album.Id,
        LibraryId = album.Id,
        Title = album.Title,
        ArtistName = album.ArtistName,
        DeezerId = album.DeezerId,
        ArtistDeezerId = album.ArtistDeezerId,
        AlbumDeezerId = album.DeezerId,
        Overview = album.Overview,
        Year = album.ReleaseDate?.Year ?? 0,
        ReleaseDate = album.ReleaseDate,
        ArtUrl = album.PosterUrl,
        BackdropUrl = album.FanartUrl,
        AlbumType = album.AlbumType,
        IsInLibrary = true,
        IsMonitored = album.Monitored,
        HasFile = album.HasFile,
        IsUnidentified = album.DeezerId <= 0,
        TrackCount = album.TotalTrackCount,
        TrackFileCount = album.TrackFileCount,
        SizeOnDisk = album.SizeOnDisk,
        AddedAt = album.Added,
        Tracks = album.Tracks.Select(t => new MusicTrackItem
        {
            Number = t.AbsoluteTrackNumber > 0 ? t.AbsoluteTrackNumber : t.TrackNumber,
            DiscNumber = t.MediumNumber,
            Title = t.Title,
            DurationSeconds = t.DurationSeconds,
            DeezerId = t.DeezerId,
            TrackId = t.Id,
            FilePath = t.FilePath,
            HasFile = t.HasFile,
            Monitored = t.Monitored,
            IsUnidentified = t.IsUnidentified,
            LyricsState = t.LyricsState,
            LyricsTier = t.LyricsTier
        }).ToList()
    };
}
