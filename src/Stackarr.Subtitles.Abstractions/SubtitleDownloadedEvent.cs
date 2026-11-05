namespace Stackarr.Subtitles.Abstractions;

/// <summary>
/// Raised after a subtitle backend downloads a subtitle. Player modules use it to refresh the
/// library so the new sidecar track is visible without waiting for a scheduled scan.
/// </summary>
public sealed record SubtitleDownloadedEvent(
    string Provider,
    string Language,
    string Title = "",
    string Subtitle = "",
    int? SeriesId = null,
    int? EpisodeId = null,
    int? MovieId = null);
