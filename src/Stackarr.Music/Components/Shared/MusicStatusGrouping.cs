using Stackarr.Music.Models;
using Stackarr.Ui.Components;

namespace Stackarr.Music.Components.Shared;

/// <summary>
/// Groups the music library into the same collapsible sections the Movies/Shows pages use. A
/// partially held album belongs with what you have, an unmonitored item was never wanted, and only a
/// genuinely wanted-but-absent item is Missing. The section vocabulary lives in the shared
/// <see cref="StatusGrouping"/>.
/// </summary>
public static class MusicStatusGrouping
{
    public static IReadOnlyList<StatusGroup<MusicItem>> ForMusic(IEnumerable<MusicItem> items) =>
        StatusGrouping.Build(items, GroupOfMusic, StatusGrouping.MusicSections);

    public static StatusGroupKind GroupOfMusic(MusicItem item) =>
        item.IsDownloading ? StatusGroupKind.Downloading
        : item.LibraryStatus is "Missing" ? StatusGroupKind.Missing
        : item.LibraryStatus is "Partial" or "Downloaded" or "Unmonitored" ? StatusGroupKind.Downloaded
        : item.HasFile ? StatusGroupKind.Downloaded
        : StatusGroupKind.Missing;
}
