namespace Stackarr.Tvarr.Services;

/// <summary>
/// What the user told the dialog before analysis. A known target (movie/show/season/episode)
/// always wins; <see cref="AutoDetect"/> asks Stackarr to identify the file first.
/// </summary>
public sealed record TvImportContext(
    bool AutoDetect,
    string MediaType,
    int TmdbId,
    int TvdbId,
    int LibraryId,
    int SeasonNumber,
    int EpisodeNumber,
    string Title = "",
    int Year = 0,
    string PosterUrl = "");

/// <summary>One uploaded file as the confirmation UI sees it.</summary>
public sealed record TvImportFileAnalysis(
    long UploadId,
    string FileName,
    long SizeBytes,
    bool IsVideo,
    bool IsSubtitle,
    TvParsedFile Parsed,
    string? ArrSuggestion,
    string? Quality,
    IReadOnlyList<string> Rejections);

/// <summary>The deterministic plus optional AI identification of an uploaded batch.</summary>
public sealed record TvImportAnalysis(
    string BatchId,
    IReadOnlyList<TvImportFileAnalysis> Files,
    IReadOnlyList<TvImportCandidate> Candidates,
    TvImportCandidate? Match,
    TvImportAiDecision? Ai,
    IReadOnlyList<string> Warnings);

/// <summary>An explicit per-file episode choice from the confirmation UI.</summary>
public sealed record TvImportFileMapping(
    long UploadId,
    int SeasonNumber,
    int EpisodeNumber,
    int EndEpisodeNumber = 0,
    int EpisodeId = 0);

/// <summary>The confirmed import request. Ids come from the candidate the user accepted.</summary>
public sealed class TvImportRequest
{
    public string BatchId { get; set; } = "";
    public string MediaType { get; set; } = "movie";
    public string Title { get; set; } = "";
    public int Year { get; set; }
    public int TmdbId { get; set; }
    public int TvdbId { get; set; }
    public int LibraryId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public List<int> MonitoredSeasons { get; set; } = [];
    public int QualityProfileId { get; set; }
    public bool ReplaceExisting { get; set; }
    public bool ImportSubtitles { get; set; } = true;
    public List<TvImportFileMapping> Mappings { get; set; } = [];
}

public sealed record TvImportFileResult(
    long UploadId,
    string FileName,
    string Status,
    string Message,
    int SeasonNumber,
    int EpisodeNumber,
    string Quality);

public sealed record TvImportResult(
    string BatchId,
    bool Success,
    string Summary,
    int MovieId,
    int SeriesId,
    IReadOnlyList<TvImportFileResult> Files);

/// <summary>The result a completed upload dialog returns to its caller.</summary>
public sealed record TvUploadOutcome(string Summary, bool Imported);

/// <summary>Shape returned by the streaming upload endpoint and consumed by the JS helper.</summary>
public sealed class TvUploadResponse
{
    public long Id { get; set; }
    public string BatchId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Status { get; set; } = "";
    public string? Error { get; set; }
    public string Kind { get; set; } = "";
    public string MediaType { get; set; } = "";
}
