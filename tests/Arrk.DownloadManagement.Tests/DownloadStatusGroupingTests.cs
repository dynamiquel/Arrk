using Arrk.DownloadManagement.Components.Shared;
using Arrk.Core.Models;
using Arrk.Ui.Components;

namespace Arrk.DownloadManagement.Tests;

/// <summary>
/// Covers the unified-downloads grouping: torrents and Soulseek transfers share one set of status
/// sections, in canonical order, with "Other" last and empty sections omitted.
/// </summary>
public class DownloadStatusGroupingTests
{
    static TorrentItem Torrent(TorrentState state) => new()
    {
        Hash = state.ToString(),
        Name = state.ToString(),
        State = state
    };

    [Test]
    public async Task TorrentGroup_MapsStates_IncludingErrorAndOther()
    {
        await Assert.That(DownloadStatusGrouping.GroupOfTorrent(Torrent(TorrentState.Downloading))).IsEqualTo(StatusGroupKind.Downloading);
        await Assert.That(DownloadStatusGrouping.GroupOfTorrent(Torrent(TorrentState.Seeding))).IsEqualTo(StatusGroupKind.Seeding);
        await Assert.That(DownloadStatusGrouping.GroupOfTorrent(Torrent(TorrentState.Paused))).IsEqualTo(StatusGroupKind.Paused);
        await Assert.That(DownloadStatusGrouping.GroupOfTorrent(Torrent(TorrentState.Error))).IsEqualTo(StatusGroupKind.Error);
        await Assert.That(DownloadStatusGrouping.GroupOfTorrent(Torrent(TorrentState.MissingFiles))).IsEqualTo(StatusGroupKind.Error);
        await Assert.That(DownloadStatusGrouping.GroupOfTorrent(Torrent(TorrentState.Queued))).IsEqualTo(StatusGroupKind.Queued);
        await Assert.That(DownloadStatusGrouping.GroupOfTorrent(Torrent(TorrentState.Unknown))).IsEqualTo(StatusGroupKind.Other);
    }

    [Test]
    public async Task Torrents_GroupInCanonicalOrder_WithOtherLast()
    {
        List<TorrentItem> torrents =
        [
            Torrent(TorrentState.Unknown),
            Torrent(TorrentState.Seeding),
            Torrent(TorrentState.Downloading),
            Torrent(TorrentState.Queued)
        ];

        IReadOnlyList<StatusGroup<TorrentItem>> groups = DownloadStatusGrouping.ForTorrents(torrents);

        await Assert.That(groups.Count).IsEqualTo(4);
        await Assert.That(groups[0].Kind).IsEqualTo(StatusGroupKind.Downloading);
        await Assert.That(groups[1].Kind).IsEqualTo(StatusGroupKind.Queued);
        await Assert.That(groups[2].Kind).IsEqualTo(StatusGroupKind.Seeding);
        await Assert.That(groups[3].Kind).IsEqualTo(StatusGroupKind.Other);
        await Assert.That(groups[3].Count).IsEqualTo(1);
        await Assert.That(groups.Sum(g => g.Count)).IsEqualTo(torrents.Count);
    }
}
