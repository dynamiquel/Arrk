using Arrk.DownloadManagement.Components.Shared;
using Arrk.DownloadManagement.Components.Tiles;
using Arrk.Ui;

namespace Arrk.DownloadManagement.Tests;

/// <summary>
/// DownloadManagement owns the storage probe behind the storage thresholds, so it also contributes
/// the free-space tile and the torrent transfer summaries to the shared tile catalogue.
/// </summary>
public class StorageTileTests
{
    static readonly DownloadManagementTileProvider Provider = new();

    [Test]
    public async Task Provider_ContributesTheStorageTileBody()
    {
        TileDefinition? storage = Provider.Tiles.FirstOrDefault(t => t.Id == "system.storage");

        await Assert.That(storage).IsNotNull();
        await Assert.That(storage!.Component).IsEqualTo(typeof(StorageTile));
        await Assert.That(storage.Href).IsEqualTo("/downloads");
    }

    [Test]
    public async Task Provider_ContributesTorrentContextTiles()
    {
        string[] ids = [.. Provider.Tiles.Select(t => t.Id)];

        await Assert.That(ids).Contains("torrents.download");
        await Assert.That(ids).Contains("torrents.upload");
        await Assert.That(ids).Contains("torrents.active");
        await Assert.That(ids).Contains("torrents.seeding");

        TileDefinition download = Provider.Tiles.First(t => t.Id == "torrents.download");
        await Assert.That(download.TitleFor(TilePlacement.Dashboard)).IsEqualTo("TORRENTS - DOWNLOAD");
        await Assert.That(download.TitleFor(TilePlacement.Page)).IsEqualTo("Download");
        await Assert.That(download.Component).IsEqualTo(typeof(TorrentStatTile));
    }
}
