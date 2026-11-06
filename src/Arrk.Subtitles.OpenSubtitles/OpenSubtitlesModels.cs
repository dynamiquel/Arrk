using System.Text.Json.Serialization;

namespace Arrk.Subtitles.OpenSubtitles;

public sealed class OpenSubtitlesSearchResponse
{
    public List<OpenSubtitlesSubtitle> Data { get; set; } = [];
}

public sealed class OpenSubtitlesSubtitle
{
    public string Id { get; set; } = "";

    public OpenSubtitlesAttributes Attributes { get; set; } = new();
}

public sealed class OpenSubtitlesAttributes
{
    public string? SubtitleId { get; set; }

    public string? Language { get; set; }

    public int DownloadCount { get; set; }

    public int NewDownloadCount { get; set; }

    public bool HearingImpaired { get; set; }

    public bool ForeignPartsOnly { get; set; }

    public bool? MachineTranslated { get; set; }

    public bool? AiTranslated { get; set; }

    public bool MoviehashMatch { get; set; }

    public string? Format { get; set; }

    public string? Release { get; set; }

    public double Ratings { get; set; }

    public string? UploadDate { get; set; }

    public OpenSubtitlesUploader? Uploader { get; set; }

    public OpenSubtitlesFeatureDetails? FeatureDetails { get; set; }

    public List<OpenSubtitlesFile> Files { get; set; } = [];
}

public sealed class OpenSubtitlesFile
{
    public int FileId { get; set; }

    public int? CdNumber { get; set; }

    public string? FileName { get; set; }
}

public sealed class OpenSubtitlesFeatureDetails
{
    public int? FeatureId { get; set; }

    public string? FeatureType { get; set; }

    public int? Year { get; set; }

    public string? Title { get; set; }

    public int? ImdbId { get; set; }

    public int? TmdbId { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }
}

public sealed class OpenSubtitlesUploader
{
    public int? UploaderId { get; set; }

    public string? Name { get; set; }

    public string? Rank { get; set; }
}

public sealed class OpenSubtitlesLanguage
{
    public string? LanguageCode { get; set; }

    public string? LanguageName { get; set; }

    public int Uploads { get; set; }
}

public sealed class OpenSubtitlesLoginResponse
{
    public string? Token { get; set; }

    public int Status { get; set; }

    public OpenSubtitlesUser? User { get; set; }
}

public sealed class OpenSubtitlesUser
{
    public int? UserId { get; set; }

    public string? Level { get; set; }

    public int? AllowedDownloads { get; set; }

    public int? RemainingDownloads { get; set; }
}

public sealed class OpenSubtitlesDownloadResponse
{
    public string? Link { get; set; }

    public string? FileName { get; set; }

    public int Remaining { get; set; }

    public int Requests { get; set; }

    public string? Message { get; set; }

    public string? ResetTime { get; set; }
}

public sealed class OpenSubtitlesErrorResponse
{
    public string? Message { get; set; }

    public string? Error { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }
}

/// <summary>Raised when OpenSubtitles rejects a request.</summary>
public sealed class OpenSubtitlesException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
