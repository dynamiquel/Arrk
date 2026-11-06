using Arrk.Core.Models;

namespace Arrk.DownloadManagement.Tests;

public class TorrentHealthTests
{
    const double OneMb = 1024 * 1024;

    [Test]
    public async Task IsIncompleteCandidate_FullTorrent_IsFalse()
    {
        TorrentItem torrent = TestConfigs.Torrent("h1", progress: 1.0, state: TorrentState.Seeding);
        await Assert.That(TorrentHealth.IsIncompleteCandidate(torrent)).IsFalse();
    }

    [Test]
    public async Task IsIncompleteCandidate_ErroredOrMissingFiles_IsFalse()
    {
        await Assert.That(TorrentHealth.IsIncompleteCandidate(
            TestConfigs.Torrent("h1", state: TorrentState.Error, progress: 0.2))).IsFalse();
        await Assert.That(TorrentHealth.IsIncompleteCandidate(
            TestConfigs.Torrent("h1", state: TorrentState.MissingFiles, progress: 0.2))).IsFalse();
    }

    [Test]
    public async Task IsIncompleteCandidate_NotDownloading_IsFalse()
    {
        await Assert.That(TorrentHealth.IsIncompleteCandidate(
            TestConfigs.Torrent("h1", state: TorrentState.Paused, progress: 0.2))).IsFalse();
        await Assert.That(TorrentHealth.IsIncompleteCandidate(
            TestConfigs.Torrent("h1", state: TorrentState.Queued, progress: 0.2))).IsFalse();
        await Assert.That(TorrentHealth.IsIncompleteCandidate(
            TestConfigs.Torrent("h1", state: TorrentState.Unknown, progress: 0.2))).IsFalse();
    }

    [Test]
    public async Task IsIncompleteCandidate_DownloadingIncomplete_IsTrue()
    {
        await Assert.That(TorrentHealth.IsIncompleteCandidate(
            TestConfigs.Torrent("h1", state: TorrentState.Downloading, progress: 0.2))).IsTrue();
    }

    [Test]
    public async Task IsStalled_DownloadingNoPeersNoSpeed_IsTrue()
    {
        await Assert.That(TorrentHealth.IsStalled(
            TestConfigs.Torrent("h1", downloadSpeed: 0, peers: 0))).IsTrue();
    }

    [Test]
    public async Task IsStalled_HasPeers_IsFalse()
    {
        await Assert.That(TorrentHealth.IsStalled(
            TestConfigs.Torrent("h1", downloadSpeed: 0, peers: 3))).IsFalse();
    }

    [Test]
    public async Task IsStalled_HasSeeds_IsFalse()
    {
        // A torrent connected to seeds is not stalled even if this tick reports zero speed.
        await Assert.That(TorrentHealth.IsStalled(
            TestConfigs.Torrent("h1", downloadSpeed: 0, peers: 0, seeds: 3))).IsFalse();
    }

    [Test]
    public async Task IsStalled_Paused_IsFalse()
    {
        await Assert.That(TorrentHealth.IsStalled(
            TestConfigs.Torrent("h1", state: TorrentState.Paused, downloadSpeed: 0, peers: 0))).IsFalse();
    }

    [Test]
    public async Task IsZeroProgress_AtOrBelowFivePercent_IsTrue()
    {
        await Assert.That(TorrentHealth.IsZeroProgress(TestConfigs.Torrent("h1", progress: 0))).IsTrue();
        await Assert.That(TorrentHealth.IsZeroProgress(TestConfigs.Torrent("h1", progress: 0.049))).IsTrue();
        await Assert.That(TorrentHealth.IsZeroProgress(TestConfigs.Torrent("h1", progress: 0.05))).IsTrue();
        await Assert.That(TorrentHealth.IsZeroProgress(TestConfigs.Torrent("h1", progress: 0.051))).IsFalse();
    }

    [Test]
    public async Task Classify_StalledTorrent_IsStalled()
    {
        TorrentHealthReason? reason = TorrentHealth.Classify(
            TestConfigs.Torrent("h1", downloadSpeed: 0, peers: 0), OneMb, slowEnabled: true, stalledEnabled: true);
        await Assert.That(reason).IsEqualTo(TorrentHealthReason.Stalled);
    }

    [Test]
    public async Task Classify_BelowThreshold_Slow()
    {
        TorrentHealthReason? reason = TorrentHealth.Classify(
            TestConfigs.Torrent("h1", downloadSpeed: 0.5 * OneMb, peers: 2), OneMb, slowEnabled: true, stalledEnabled: true);
        await Assert.That(reason).IsEqualTo(TorrentHealthReason.Slow);
    }

    [Test]
    public async Task Classify_AboveThreshold_IsNull()
    {
        TorrentHealthReason? reason = TorrentHealth.Classify(
            TestConfigs.Torrent("h1", downloadSpeed: 5 * OneMb, peers: 2), OneMb, slowEnabled: true, stalledEnabled: true);
        await Assert.That(reason).IsNull();
    }

    [Test]
    public async Task Classify_StalledDisabled_FallsBackToSlow()
    {
        TorrentHealthReason? reason = TorrentHealth.Classify(
            TestConfigs.Torrent("h1", downloadSpeed: 0, peers: 0), OneMb, slowEnabled: true, stalledEnabled: false);
        await Assert.That(reason).IsEqualTo(TorrentHealthReason.Slow);
    }

    [Test]
    public async Task Classify_QueuedTorrent_IsNull()
    {
        TorrentHealthReason? reason = TorrentHealth.Classify(
            TestConfigs.Torrent("h1", state: TorrentState.Queued, downloadSpeed: 0, peers: 0), OneMb, slowEnabled: true, stalledEnabled: true);
        await Assert.That(reason).IsNull();
    }

    [Test]
    public async Task Classify_CompleteTorrent_IsNull()
    {
        TorrentHealthReason? reason = TorrentHealth.Classify(
            TestConfigs.Torrent("h1", progress: 1.0, state: TorrentState.Seeding), OneMb, slowEnabled: true, stalledEnabled: true);
        await Assert.That(reason).IsNull();
    }
}
