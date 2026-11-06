using System.Text.Json.Serialization;
using Arrk.Tvarr.Clients;

namespace Arrk.Tvarr.Models;

public class SystemStatus
{
    public string Version { get; set; } = "";
    public string InstanceName { get; set; } = "";
}

public class QualityProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<object> Items { get; set; } = [];
    public bool UpgradeAllowed { get; set; } = true;
    public int Cutoff { get; set; }
    public List<object> FormatItems { get; set; } = [];
    public int MinFormatScore { get; set; }
    public int CutoffFormatScore { get; set; }
    public int MinUpgradeFormatScore { get; set; } = 1;
    public LanguageDto? Language { get; set; }
}

public class RootFolder
{
    public int Id { get; set; }
    public string Path { get; set; } = "";
}

public class QualityDefinitionDto
{
    public int Id { get; set; }
    public QualityDto Quality { get; set; } = new();
    public string Title { get; set; } = "";
    public int Weight { get; set; }
    public double? MinSize { get; set; }
    public double? MaxSize { get; set; }
    public double? PreferredSize { get; set; }
}

public class QualityDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public int Resolution { get; set; }

    /// <summary>
    /// Radarr sends a modifier (<c>none</c>, <c>remux</c>, …). Sonarr has no modifier field at all,
    /// so the value stays <c>null</c> and is omitted from the write-back instead of being sent as
    /// an empty string.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Modifier { get; set; }
}

public class QualityValueDto
{
    public string Name { get; set; } = "";
}

public class ImageDto
{
    public string CoverType { get; set; } = "";
    public string Url { get; set; } = "";
    public string RemoteUrl { get; set; } = "";
}

public class TagDto
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

public class CustomFormatDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IncludeCustomFormatWhenRenaming { get; set; }
    public List<object> Specifications { get; set; } = [];
}

public class DownloadClientDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Implementation { get; set; } = "";
    public string ConfigContract { get; set; } = "";
    // Radarr/Sonarr's wire field is "enable" (the schema confirms it); without this the value never
    // deserializes and the sync job treats an already-enabled client as misconfigured.
    [JsonPropertyName("enable")]
    public bool Enabled { get; set; }
    public bool EnableCompletedDownloads { get; set; }
    public bool RemoveCompletedDownloads { get; set; }
    public bool RemoveFailedDownloads { get; set; }
    public List<DownloadClientFieldDto> Fields { get; set; } = [];
}

public class DownloadClientFieldDto
{
    public string Name { get; set; } = "";
    [JsonPropertyName("value")]
    public object? Value { get; set; }
}

public class LanguageDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Radarr/Sonarr v3 return ratings as nested objects (imdb/tmdb/rottenTomatoes/metacritic),
/// not a flat value. <see cref="Value"/> picks the best available so Arrk and TMDB agree.
/// </summary>
public class RatingsDto
{
    public RatingValueDto? Imdb { get; set; }
    public RatingValueDto? Tmdb { get; set; }
    public RatingValueDto? RottenTomatoes { get; set; }
    public RatingValueDto? Metacritic { get; set; }

    [JsonIgnore]
    public double Value =>
        Tmdb?.Value > 0 ? Tmdb.Value
        : Imdb?.Value > 0 ? Imdb.Value
        : RottenTomatoes?.Value > 0 ? RottenTomatoes.Value
        : Metacritic?.Value ?? 0;

    [JsonIgnore]
    public int Votes => Tmdb?.Votes > 0 ? Tmdb.Votes : Imdb?.Votes ?? 0;
}

public class RatingValueDto
{
    public double Value { get; set; }
    public int Votes { get; set; }
    public string? Type { get; set; }
}

/// <summary>
/// The subset of a file's scan that Radarr/Sonarr expose over their API. Mirrors the arr
/// <c>mediaInfo</c> object; fields not provided by the arrs (container, video profile/level,
/// colour space, audio sample rate, per-subtitle details) are filled by an ffprobe scan.
/// </summary>
public class MediaInfoDto
{
    public long AudioBitrate { get; set; }
    public double AudioChannels { get; set; }
    public string AudioCodec { get; set; } = "";
    public string AudioLanguages { get; set; } = "";
    public int AudioStreamCount { get; set; }
    public int VideoBitDepth { get; set; }
    public long VideoBitrate { get; set; }
    public string VideoCodec { get; set; } = "";
    public double VideoFps { get; set; }
    public string VideoDynamicRange { get; set; } = "";
    public string VideoDynamicRangeType { get; set; } = "";

    /// <summary>Coded resolution as reported by the arr, e.g. <c>1920x1080</c>.</summary>
    public string Resolution { get; set; } = "";

    public string RunTime { get; set; } = "";
    public string ScanType { get; set; } = "";
    public string Subtitles { get; set; } = "";
    public string ContainerFormat { get; set; } = "";
}

/// <summary>The nested <c>quality</c> object on a movie/episode file resource.</summary>
public class FileQualityDto
{
    public QualityDto? Quality { get; set; }
    public QualityRevisionDto? Revision { get; set; }
}

/// <summary>
/// The <c>revision</c> object nested inside a file's <c>quality</c>. The arrs send an object
/// here (version / real / isRepack), not a bare integer.
/// </summary>
public class QualityRevisionDto
{
    public int Version { get; set; }
    public int Real { get; set; }
    public bool IsRepack { get; set; }
}

public class HistoryDto
{
    public int Id { get; set; }
    public string EventType { get; set; } = "";
    public DateTime Date { get; set; }

    /// <summary>The download client's id (torrent hash) for this history event, when present.</summary>
    public string DownloadId { get; set; } = "";

    /// <summary>Release name as the indexer published it (Radarr/Sonarr history).</summary>
    public string SourceTitle { get; set; } = "";

    /// <summary>Owning movie id (Radarr history only).</summary>
    public int? MovieId { get; set; }

    /// <summary>Owning series id (Sonarr history only).</summary>
    public int? SeriesId { get; set; }

    /// <summary>Owning episode id (Sonarr history only).</summary>
    public int? EpisodeId { get; set; }
}

/// <summary>
/// A release returned by the arr's interactive-search endpoint (<c>GET /api/v3/release</c>).
/// Radarr and Sonarr share the shape; Sonarr additionally populates <see cref="ReleaseType"/>.
/// </summary>
public class ReleaseResourceDto
{
    public string Guid { get; set; } = "";
    public string Title { get; set; } = "";
    public int IndexerId { get; set; }
    public string Indexer { get; set; } = "";
    public long Size { get; set; }
    public int Seeders { get; set; }
    public int Leechers { get; set; }
    public string Protocol { get; set; } = "";
    public DateTime? PublishDate { get; set; }
    public FileQualityDto? Quality { get; set; }

    /// <summary>Sum of the custom-format scores the arr's quality profile applied to this release.</summary>
    public int CustomFormatScore { get; set; }

    public List<CustomFormatItemDto> CustomFormats { get; set; } = [];
    public bool Rejected { get; set; }
    public List<string> Rejections { get; set; } = [];
    public string DownloadUrl { get; set; } = "";
    public string InfoUrl { get; set; } = "";
    public string InfoHash { get; set; } = "";
    public int MovieId { get; set; }
    public int SeriesId { get; set; }
    public int EpisodeId { get; set; }

    /// <summary>Sonarr only: <c>singleEpisode</c> or <c>seasonPack</c>.</summary>
    public string ReleaseType { get; set; } = "";

    public List<LanguageDto> Languages { get; set; } = [];
}

/// <summary>A custom format the arr matched on a release (used to explain its score).</summary>
public class CustomFormatItemDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class QueueItemDto
{
    public int Id { get; set; }
    public string Status { get; set; } = "";
    public DateTime Added { get; set; }
}

public class QueuePagingResource<T>
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalRecords { get; set; }
    public List<T> Records { get; set; } = [];
}

public class CalendarDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public bool HasFile { get; set; }
}

/// <summary>
/// One row from the arr's <c>/api/v3/manualimport</c> endpoint. Radarr fills <see cref="Movie"/>,
/// Sonarr fills <see cref="Series"/> and <see cref="Episodes"/>; everything else is shared.
/// </summary>
public class ManualImportResourceDto
{
    public string Path { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public FileQualityDto? Quality { get; set; }
    public List<LanguageDto> Languages { get; set; } = [];
    public string? ReleaseGroup { get; set; }
    public int? SeasonNumber { get; set; }
    public string? ReleaseType { get; set; }
    public int IndexerFlags { get; set; }
    public List<ImportRejectionDto> Rejections { get; set; } = [];
    public MovieDto? Movie { get; set; }
    public SeriesDto? Series { get; set; }
    public List<EpisodeDto> Episodes { get; set; } = [];
}

public class ImportRejectionDto
{
    public string Reason { get; set; } = "";
    public string Type { get; set; } = "";
}

/// <summary>The subset of an arr command resource Arrk polls after a manual import.</summary>
public class ArrCommandDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string Result { get; set; } = "";
    public string? Message { get; set; }
    public string? Exception { get; set; }
    public DateTime? Queued { get; set; }
    public DateTime? Started { get; set; }
    public DateTime? Ended { get; set; }
}

/// <summary>
/// One file in a <c>ManualImport</c> command. Null fields are omitted so Radarr and Sonarr each
/// receive only the keys their own command model understands.
/// </summary>
public class ManualImportFileDto
{
    public string Path { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MovieId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SeriesId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SeasonNumber { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? EpisodeIds { get; set; }

    public FileQualityDto? Quality { get; set; }
    public List<LanguageDto>? Languages { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReleaseGroup { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReleaseType { get; set; }

    public int IndexerFlags { get; set; }
}
