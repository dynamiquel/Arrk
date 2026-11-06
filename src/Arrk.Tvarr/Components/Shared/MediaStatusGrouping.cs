using Arrk.Ui.Components;

namespace Arrk.Tvarr.Components.Shared;

/// <summary>
/// Maps movie/show catalog items to a single primary status and groups them into the collapsible
/// sections used by the Movies and TV Shows pages. The section vocabulary and bucketing live in the
/// shared <see cref="StatusGrouping"/>; this supplies only the TV classifiers and the search
/// relevance ordering.
/// </summary>
public static class MediaStatusGrouping
{
    /// <summary>Movies: an active queue download wins, then missing, then downloaded.</summary>
    public static StatusGroupKind GroupOfMovie(MediaItem item) =>
        item.IsDownloading ? StatusGroupKind.Downloading
        : !item.HasFile ? StatusGroupKind.Missing
        : StatusGroupKind.Downloaded;

    /// <summary>
    /// Shows: an active queue download wins, then the series library status. A partially
    /// downloaded show is grouped with downloaded ones, as far as the user is concerned
    /// both mean "I have this".
    /// </summary>
    public static StatusGroupKind GroupOfSeries(MediaItem item)
    {
        if (item.IsDownloading)
        {
            return StatusGroupKind.Downloading;
        }

        return item.LibraryStatus switch
        {
            "Missing" => StatusGroupKind.Missing,
            _ => StatusGroupKind.Downloaded
        };
    }

    public static IReadOnlyList<StatusGroup<MediaItem>> ForMovies(IEnumerable<MediaItem> items) =>
        StatusGrouping.Build(items, GroupOfMovie, StatusGrouping.LibrarySections);

    public static IReadOnlyList<StatusGroup<MediaItem>> ForSeries(IEnumerable<MediaItem> items) =>
        StatusGrouping.Build(items, GroupOfSeries, StatusGrouping.LibrarySections);

    /// <summary>
    /// Relevance score for a search match: a title prefix beats a title substring beats an
    /// overview-only match.
    /// </summary>
    public static int SearchScore(MediaItem item, string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return 0;
        }

        if (item.Title.StartsWith(term, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (item.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return item.Overview.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    /// <summary>Orders search hits most-relevant first, breaking ties by title.</summary>
    public static IReadOnlyList<MediaItem> OrderBySearchRelevance(IEnumerable<MediaItem> items, string term)
    {
        string trimmed = term.Trim();
        return items
            .Select(item => (Item: item, Score: SearchScore(item, trimmed)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Item)
            .ToList();
    }
}
