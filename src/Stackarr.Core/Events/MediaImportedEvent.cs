namespace Stackarr.Core.Events;

/// <summary>
/// Raised after a movie or episode file lands in the library. The optional library ids let
/// consumers such as the subtitle watcher act on the exact item without scanning the library.
/// </summary>
public sealed record MediaImportedEvent(
    string Source,
    string SourceName,
    string ItemType,
    int? MovieId = null,
    int? SeriesId = null,
    IReadOnlyList<int>? EpisodeIds = null,
    MediaEntityRef? Entity = null);
