using Arrk.Music.Components.Shared;
using Arrk.Music.Models;
using Arrk.Ui.Components;

namespace Arrk.Music.Tests;

/// <summary>
/// Covers the music library grouping into the collapsible sections: an active download first, a
/// partially held or unmonitored item with what you have, and only a genuinely wanted-but-absent
/// item as Missing.
/// </summary>
public class MusicStatusGroupingTests
{
    static MusicItem Song(bool hasFile, bool downloading = false) => new()
    {
        EntityType = MusicEntityType.Track,
        Title = hasFile ? "Downloaded" : "Missing",
        HasFile = hasFile,
        IsDownloading = downloading
    };

    static MusicItem MusicAlbum(string status, bool downloading = false, bool hasFile = false) => new()
    {
        EntityType = MusicEntityType.Album,
        Title = status,
        LibraryStatus = status,
        IsDownloading = downloading,
        HasFile = hasFile
    };

    [Test]
    public async Task Music_GroupedIntoDownloadingThenDownloadedThenMissing()
    {
        List<MusicItem> songs = [Song(hasFile: true), Song(hasFile: false, downloading: true), Song(hasFile: false)];

        IReadOnlyList<StatusGroup<MusicItem>> groups = MusicStatusGrouping.ForMusic(songs);

        await Assert.That(groups.Count).IsEqualTo(3);
        await Assert.That(groups[0].Kind).IsEqualTo(StatusGroupKind.Downloading);
        await Assert.That(groups[1].Kind).IsEqualTo(StatusGroupKind.Downloaded);
        await Assert.That(groups[2].Kind).IsEqualTo(StatusGroupKind.Missing);
        await Assert.That(groups.Sum(g => g.Count)).IsEqualTo(songs.Count);
    }

    [Test]
    public async Task MusicGroup_FoldsPartialAndUnmonitoredIntoDownloaded_OnlyMissingIsMissing()
    {
        // A partially held album belongs with what you have; an unmonitored item was never wanted,
        // so it is not "Missing" either. Only a genuinely wanted-but-absent item is Missing.
        await Assert.That(MusicStatusGrouping.ForMusic([MusicAlbum("Partial")])[0].Kind).IsEqualTo(StatusGroupKind.Downloaded);
        await Assert.That(MusicStatusGrouping.ForMusic([MusicAlbum("Unmonitored")])[0].Kind).IsEqualTo(StatusGroupKind.Downloaded);
        await Assert.That(MusicStatusGrouping.ForMusic([MusicAlbum("Missing")])[0].Kind).IsEqualTo(StatusGroupKind.Missing);
        await Assert.That(MusicStatusGrouping.ForMusic([MusicAlbum("Missing", downloading: true)])[0].Kind).IsEqualTo(StatusGroupKind.Downloading);
    }
}
