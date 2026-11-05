namespace Stackarr.Tvarr.Models;

/// <summary>
/// One locally uploaded file waiting to be imported by Radarr or Sonarr. A batch groups the files
/// selected together; the first file in a movie upload and every episode file in a season upload
/// share a <see cref="BatchId"/>.
/// </summary>
public class TvUploadRecord
{
    public long Id { get; set; }

    /// <summary>Groups files uploaded together (for example every episode of a season).</summary>
    public string BatchId { get; set; } = "";

    public string FileName { get; set; } = "";

    /// <summary>Absolute path as Stackarr sees it. Used to stream/copy and to clean up.</summary>
    public string StoredPath { get; set; } = "";

    /// <summary>Path as Radarr/Sonarr see it, always under their <c>/downloads</c> mount.</summary>
    public string ContainerPath { get; set; } = "";

    public long SizeBytes { get; set; }

    /// <summary>One of <see cref="TvUploadStatus"/>.</summary>
    public string Status { get; set; } = TvUploadStatus.Staged;

    public string? Error { get; set; }

    /// <summary>movie, series, season, episode or auto.</summary>
    public string Kind { get; set; } = "auto";

    /// <summary>movie or tv.</summary>
    public string MediaType { get; set; } = "";

    public int TmdbId { get; set; }
    public int TvdbId { get; set; }
    public int LibraryId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class TvUploadStatus
{
    /// <summary>The file is on disk and the arr can read it.</summary>
    public const string Staged = "staged";

    /// <summary>The arr is parsing/importing it.</summary>
    public const string Importing = "importing";

    public const string Imported = "imported";

    public const string Failed = "failed";

    /// <summary>Deleted from staging because the user cancelled or the retention window passed.</summary>
    public const string Removed = "removed";

    public static bool IsTerminal(string status) =>
        status is Imported or Failed or Removed;
}
