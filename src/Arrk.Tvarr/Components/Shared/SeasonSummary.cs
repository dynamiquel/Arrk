namespace Arrk.Tvarr.Components.Shared;

/// <summary>
/// View-model helpers for the series dialog's season selector: the per-season progress badge
/// shown on the season tabs, and which season should be open when the dialog loads.
/// </summary>
public static class SeasonSummary
{
    /// <summary>
    /// Progress label and chip variant for a season tab (e.g. <c>7/10</c> with a warning tint).
    /// Returns nulls when the season has no known episodes (e.g. a remote series) so the tab
    /// renders no badge at all.
    /// </summary>
    public static (string? Count, string? Variant) Progress(int downloaded, int available)
    {
        if (available <= 0)
            return (null, null);

        string variant = downloaded >= available ? "success"
            : downloaded > 0 ? "warning"
            : "neutral";
        return ($"{downloaded}/{available}", variant);
    }

    /// <summary>
    /// The season to open by default: the first season that already has episodes loaded,
    /// otherwise the lowest-numbered season. Specials (season 0) are ignored. Returns
    /// <c>null</c> when there are no seasons.
    /// </summary>
    public static int? DefaultSeason(IEnumerable<int> seasons, IEnumerable<int> loadedSeasons)
    {
        List<int> ordered = seasons.Where(number => number > 0).Distinct().OrderBy(number => number).ToList();
        if (ordered.Count == 0)
            return null;

        HashSet<int> loaded = loadedSeasons.ToHashSet();
        return ordered.FirstOrDefault(number => loaded.Contains(number), ordered[0]);
    }
}
