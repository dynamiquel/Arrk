namespace Arrk.Ui;

/// <summary>
/// Identifies the item a player wants subtitles for. The player only knows its own opaque item id
/// plus the provider ids the media server exposes; the subtitles module resolves those to its own
/// library ids.
/// </summary>
public sealed record PlayerSubtitleSearchRequest(
    string Title,
    string Subtitle,
    bool IsMovie,
    int TmdbId = 0,
    int TvdbId = 0,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    IReadOnlyList<string>? Languages = null);

/// <summary>
/// Lets the player ask the subtitle module to open its normal search popup without the player
/// referencing a subtitle backend (the dependency runs the other way). The shared subtitle module
/// contributes a component to <see cref="UiRegions.PlayerOverlays"/> that handles requests.
/// </summary>
public sealed class SubtitleSearchLauncher
{
    /// <summary>Raised when the player asks for subtitle search. Null when no subtitle module is available.</summary>
    public event Func<PlayerSubtitleSearchRequest, Task>? Requested;

    public bool IsAvailable => Requested is not null;

    public Task ShowAsync(PlayerSubtitleSearchRequest request) =>
        Requested?.Invoke(request) ?? Task.CompletedTask;
}
