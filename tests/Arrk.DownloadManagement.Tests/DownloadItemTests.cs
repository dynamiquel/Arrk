using Arrk.Core.Models;
using Arrk.DownloadManagement.Components.Shared;
using Arrk.Ui.Components;
using Arrk.Slskd;

namespace Arrk.DownloadManagement.Tests;

/// <summary>Covers the unified Downloads mapping so Soulseek transfers show under the right group
/// (notably that an in-progress download shows as Downloading, not Queued/Other).</summary>
public class DownloadItemTests
{
    [Test]
    public async Task FromSoulseek_MapsStatesToTheRightGroup()
    {
        DownloadItem downloading = DownloadItem.FromSoulseek(new SlskdDownload
        {
            State = "InProgress",
            StartedAt = DateTime.UtcNow,
            PercentComplete = 42
        });
        await Assert.That(downloading.StateKind).IsEqualTo(StatusGroupKind.Downloading);
        await Assert.That(downloading.SourceLabel).IsEqualTo("Soulseek");

        DownloadItem queued = DownloadItem.FromSoulseek(new SlskdDownload { State = "Queued, Remotely" });
        await Assert.That(queued.StateKind).IsEqualTo(StatusGroupKind.Queued);

        DownloadItem complete = DownloadItem.FromSoulseek(new SlskdDownload { State = "Completed, Succeeded", PercentComplete = 100 });
        await Assert.That(complete.StateKind).IsEqualTo(StatusGroupKind.Downloaded);

        DownloadItem failed = DownloadItem.FromSoulseek(new SlskdDownload { State = "Completed, Errored", Exception = "no peer" });
        await Assert.That(failed.StateKind).IsEqualTo(StatusGroupKind.Error);
    }

    [Test]
    public async Task PercentDisplay_FloorsInsteadOfRoundingUp()
    {
        DownloadItem almostDone = DownloadItem.FromTorrent(new TorrentItem
        {
            Hash = "h1",
            Name = "Tomb Raider King S01E04",
            Progress = 0.9996856451034546,
            State = TorrentState.Downloading
        });
        await Assert.That(almostDone.PercentDisplay).IsEqualTo(99.9);

        DownloadItem complete = DownloadItem.FromTorrent(new TorrentItem
        {
            Hash = "h2",
            Name = "done",
            Progress = 1.0,
            State = TorrentState.Seeding
        });
        await Assert.That(complete.PercentDisplay).IsEqualTo(100.0);
    }
}
