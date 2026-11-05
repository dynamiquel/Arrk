namespace Stackarr.Media.Abstractions;

/// <summary>
/// A stable reference to one media entity (a movie, series, season, episode or album) that can
/// travel with an event without the receiving component knowing which module owns it. The
/// provider key and item key are module-owned values; the media item key helpers in Core produce
/// the built-in TV/music forms.
/// </summary>
/// <param name="ProviderKey">Media service key, e.g. <c>tv</c> or <c>music</c>.</param>
/// <param name="Domain">Broad media domain.</param>
/// <param name="ItemKey">Stable module-owned key, e.g. <c>tv:series:tvdb:123:s2e5</c>.</param>
/// <param name="Title">Display title for cards and notifications.</param>
/// <param name="Subtitle">Optional scope/subtitle, e.g. "Season 2" or "2024".</param>
/// <param name="PosterUrl">Absolute or app-relative poster URL.</param>
/// <param name="BackdropUrl">Absolute or app-relative backdrop URL.</param>
/// <param name="ParentItemKey">Immediate container key, e.g. a season for an episode.</param>
/// <param name="AncestorItemKeys">All container keys, outermost first or any order; used for subscription matching.</param>
public sealed record MediaEntityRef(
    string ProviderKey,
    MediaDomain Domain,
    string ItemKey,
    string Title,
    string? Subtitle = null,
    string? PosterUrl = null,
    string? BackdropUrl = null,
    string? ParentItemKey = null,
    IReadOnlyList<string>? AncestorItemKeys = null,
    string? Url = null)
{
    /// <summary>The ancestor keys, never null.</summary>
    public IReadOnlyList<string> Ancestors => AncestorItemKeys ?? [];
}
