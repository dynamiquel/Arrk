namespace Stackarr.Media.Abstractions;

/// <summary>
/// The descriptive facts shared by every media item a library, metadata provider or UI card can
/// expose. Implement only the capabilities an item actually has; a movie search result and a fully
/// populated library row both satisfy this interface.
/// </summary>
public interface IMediaDescriptor
{
    string Title { get; }

    string OriginalTitle { get; }

    int Year { get; }

    DateTime? ReleaseDate { get; }

    string Overview { get; }

    IReadOnlyList<string> Genres { get; }

    double? Rating { get; }

    int? RuntimeMinutes { get; }

    string Certification { get; }

    string OriginalLanguage { get; }
}

/// <summary>Artwork URLs shared by every media shape.</summary>
public interface IMediaArtwork
{
    string PosterUrl { get; }

    string FanartUrl { get; }

    string BannerUrl { get; }

    string LogoUrl { get; }
}

/// <summary>
/// The state a library item can expose. Metadata-only results do not implement this; a provider
/// that can answer "is it monitored / downloaded?" does.
/// </summary>
public interface IMediaLibraryState
{
    bool Monitored { get; }

    bool HasFile { get; }

    long SizeOnDisk { get; }

    DateTime AddedAt { get; }

    QualityInfo? Quality { get; }

    MediaFileInfo? File { get; }
}

/// <summary>Live download state for a card or detail view.</summary>
public interface IMediaDownloadState
{
    bool IsDownloading { get; }

    long DownloadSize { get; }

    long DownloadSizeLeft { get; }

    string? DownloadStatus { get; }

    string? DownloadTitle { get; }

    double DownloadPercent => DownloadSize > 0
        ? Math.Clamp((DownloadSize - DownloadSizeLeft) / (double)DownloadSize * 100, 0, 100)
        : 0;
}

/// <summary>Watch state projected from a player module.</summary>
public interface IMediaWatchState
{
    double WatchProgress { get; }

    bool IsWatched { get; }

    long ResumeTicks { get; }

    DateTime? LastPlayed { get; }

    bool HasWatchProgress => WatchProgress > 0.001 || IsWatched;
}

/// <summary>Request state attached to a library/discovery card.</summary>
public interface IMediaRequestState
{
    string RequestState { get; }

    bool HasRequestState => !string.IsNullOrEmpty(RequestState);
}
