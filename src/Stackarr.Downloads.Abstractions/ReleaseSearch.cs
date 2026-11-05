using Stackarr.Media.Abstractions;

namespace Stackarr.Downloads.Abstractions;

/// <summary>
/// Domain-agnostic description of the thing being searched for. <see cref="Domain"/> and
/// <see cref="Kind"/> are open values owned by the media module; a provider reads the ids and
/// context fields its source understands and ignores the rest.
/// </summary>
public record ReleaseSearchQuery
{
    /// <summary>Broad media domain, e.g. the TV or music module's own key.</summary>
    public MediaDomain Domain { get; init; }

    /// <summary>Feature-owned release kind (movie, season, episode, album, track, ...).</summary>
    public string Kind { get; init; } = "";

    /// <summary>Display title (movie/series/album name) for the picker header.</summary>
    public string Title { get; init; } = "";

    /// <summary>Human noun used in the picker, e.g. "a movie", "TV", "music".</summary>
    public string Noun { get; init; } = "releases";

    /// <summary>Provider ids the source may understand (tmdb, tvdb, deezer, ...).</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The primary library id for this target: a movie/series/album id, or an episode id when
    /// <see cref="ParentLibraryId"/> carries the series.
    /// </summary>
    public string? LibraryId { get; init; }

    /// <summary>A container library id when the target is a child (series for a season/episode).</summary>
    public string? ParentLibraryId { get; init; }

    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }

    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";

    public string? GetProviderId(string key) =>
        ProviderIds.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    public int GetProviderIdInt(string key) =>
        int.TryParse(GetProviderId(key), out int value) ? value : 0;

    public int GetLibraryIdInt() => int.TryParse(LibraryId, out int value) ? value : 0;

    public int GetParentLibraryIdInt() => int.TryParse(ParentLibraryId, out int value) ? value : 0;
}

/// <summary>
/// A single candidate release, normalised across Radarr/Sonarr (arr release resources) and the
/// Stackarr-native music search. Each provider fills the grab keys its download path needs.
/// </summary>
public record ReleaseCandidate
{
    /// <summary>Stable identity for the row (arr guid, or infohash/title for music).</summary>
    public required string Id { get; init; }

    public string Title { get; init; } = "";
    public string Indexer { get; init; } = "";

    /// <summary>The provider/source that produced this candidate (e.g. "Radarr", "Soulseek").</summary>
    public string Source { get; init; } = "";

    /// <summary>Torrent or usenet, as reported by the source (defaults to torrent).</summary>
    public string Protocol { get; init; } = "torrent";

    public long Size { get; init; }
    public int Seeders { get; init; }
    public int Leechers { get; init; }
    public DateTime PublishDate { get; init; }

    /// <summary>Source relevance, arr custom-format score, or Stackarr's music match score.</summary>
    public int Score { get; init; }

    /// <summary>Human quality label, e.g. "Bluray-1080p" or "Lossless".</summary>
    public string QualityLabel { get; init; } = "";

    /// <summary>Format chips the arr's quality profile cares about (DV, HDR10+, Atmos, …).</summary>
    public IReadOnlyList<string> FormatTags { get; init; } = [];

    public bool Rejected { get; init; }
    public IReadOnlyList<string> Rejections { get; init; } = [];

    // Grab keys, arr providers use Guid + IndexerId; music uses the url/hash fields.
    public int IndexerId { get; init; }
    public string Guid { get; init; } = "";
    public string MagnetUrl { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string InfoHash { get; init; } = "";
    public bool IsLossless { get; init; }

    /// <summary>
    /// Concrete files a grab should enqueue (Soulseek/slskd). Empty for arr/Prowlarr candidates,
    /// whose grab keys are the magnet/url above.
    /// </summary>
    public IReadOnlyList<ReleaseSourceFile> SourceFiles { get; init; } = [];

    /// <summary>True when the release comes from a usenet indexer rather than a torrent tracker.</summary>
    public bool IsUsenet => !string.Equals(Protocol, "torrent", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The outcome of grabbing a candidate release.</summary>
public record ReleaseGrabResult(bool Success, ReleaseCandidate? Release, string Message);

/// <summary>A single downloadable file inside a candidate (used by the Soulseek source).</summary>
public record ReleaseSourceFile(string Peer, string Filename, long Size);
