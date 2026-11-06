using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.TestSupport;

namespace Arrk.Transmission.Tests;

public class TransmissionServiceTests
{
    static TransmissionService CreateService(
        FakeTransmissionClient client,
        TorrentDownloaderConfig? config = null,
        MutableOptionsMonitor<TorrentDownloaderConfig>? monitor = null)
        => new(client,
            monitor ?? new MutableOptionsMonitor<TorrentDownloaderConfig>(config ?? new TorrentDownloaderConfig()),
            NullLogger<TransmissionService>.Instance);

    static TransmissionTorrent Torrent(long id, int status, int error = 0, double percent = 0.5) => new()
    {
        Id = id,
        Name = $"torrent-{id}",
        HashString = $"hash-{id}",
        Status = status,
        Error = error,
        PercentDone = percent,
        RateDownload = 100,
        RateUpload = 50,
        UploadRatio = 1.25,
        TotalSize = 1000,
        LeftUntilDone = 400,
        AddedDate = 1_700_000_000
    };

    // Mapping

    [Test]
    public async Task GetTorrents_MapsStatusToState()
    {
        FakeTransmissionClient client = new()
        {
            Torrents =
            [
                Torrent(1, TransmissionStatus.Stopped),
                Torrent(2, TransmissionStatus.CheckWait),
                Torrent(3, TransmissionStatus.Checking),
                Torrent(4, TransmissionStatus.DownloadWait),
                Torrent(5, TransmissionStatus.Downloading),
                Torrent(6, TransmissionStatus.SeedWait),
                Torrent(7, TransmissionStatus.Seeding)
            ]
        };
        TransmissionService service = CreateService(client);

        List<TorrentItem> torrents = await service.GetTorrents();

        await Assert.That(torrents[0].State).IsEqualTo(TorrentState.Paused);
        await Assert.That(torrents[1].State).IsEqualTo(TorrentState.Queued);
        await Assert.That(torrents[2].State).IsEqualTo(TorrentState.Downloading);
        await Assert.That(torrents[3].State).IsEqualTo(TorrentState.Queued);
        await Assert.That(torrents[4].State).IsEqualTo(TorrentState.Downloading);
        await Assert.That(torrents[5].State).IsEqualTo(TorrentState.Queued);
        await Assert.That(torrents[6].State).IsEqualTo(TorrentState.Seeding);
    }

    [Test]
    public async Task GetTorrents_ErrorTorrent_MapsToError()
    {
        FakeTransmissionClient client = new()
        {
            Torrents = [Torrent(1, TransmissionStatus.Checking, error: 3)]
        };
        TransmissionService service = CreateService(client);

        List<TorrentItem> torrents = await service.GetTorrents();

        await Assert.That(torrents[0].State).IsEqualTo(TorrentState.Error);
    }

    [Test]
    public async Task GetTorrents_MapsFields()
    {
        FakeTransmissionClient client = new() { Torrents = [Torrent(1, TransmissionStatus.Downloading)] };
        TransmissionService service = CreateService(client);

        TorrentItem item = (await service.GetTorrents())[0];

        await Assert.That(item.Hash).IsEqualTo("hash-1");
        await Assert.That(item.Name).IsEqualTo("torrent-1");
        await Assert.That(item.DownloadSpeed).IsEqualTo(100);
        await Assert.That(item.UploadSpeed).IsEqualTo(50);
        await Assert.That(item.Ratio).IsEqualTo(1.25);
        await Assert.That(item.Size).IsEqualTo(1000);
        await Assert.That(item.Downloaded).IsEqualTo(600);
    }

    [Test]
    public async Task GetTorrents_UnknownFilter_PassesNullFilter()
    {
        FakeTransmissionClient client = new();
        TransmissionService service = CreateService(client);

        await service.GetTorrents("something-weird");

        await Assert.That(client.LastFilter).IsNull();
    }

    [Test]
    public async Task GetTorrents_DownloadingFilter_PassesFilterDictionary()
    {
        FakeTransmissionClient client = new();
        TransmissionService service = CreateService(client);

        await service.GetTorrents("downloading");

        await Assert.That(client.LastFilter).IsNotNull();
        await Assert.That(client.LastFilter!.ContainsKey("downloading")).IsTrue();
        await Assert.That(client.LastFilter["downloading"]).IsTrue();
    }

    [Test]
    public async Task GetTorrent_FindsByHash_CaseInsensitive()
    {
        FakeTransmissionClient client = new() { Torrents = [Torrent(1, TransmissionStatus.Downloading)] };
        TransmissionService service = CreateService(client);

        TorrentItem? found = await service.GetTorrent("HASH-1");
        TorrentItem? missing = await service.GetTorrent("nope");

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Hash).IsEqualTo("hash-1");
        await Assert.That(missing).IsNull();
    }

    [Test]
    public async Task GetTorrents_ClientException_Propagates()
    {
        FakeTransmissionClient client = new() { GetTorrentsException = new HttpRequestException("boom") };
        TransmissionService service = CreateService(client);

        await Assert.That(async () => await service.GetTorrents())
            .Throws<HttpRequestException>();
    }

    [Test]
    public async Task GetTorrents_UnknownStatus_MapsToUnknown()
    {
        FakeTransmissionClient client = new() { Torrents = [Torrent(1, 99)] };
        TransmissionService service = CreateService(client);

        List<TorrentItem> torrents = await service.GetTorrents();

        await Assert.That(torrents[0].State).IsEqualTo(TorrentState.Unknown);
    }

    // Add / delete / pause

    [Test]
    public async Task AddTorrent_ReturnsIdString()
    {
        FakeTransmissionClient client = new() { AddResult = 77 };
        TransmissionService service = CreateService(client);

        string id = await service.AddTorrent([1, 2, 3]);

        await Assert.That(id).IsEqualTo("77");
        await Assert.That(client.AddBytesCalls).IsEqualTo(1);
    }

    [Test]
    public async Task AddTorrent_ByUri_ReturnsIdString()
    {
        FakeTransmissionClient client = new() { AddResult = 8 };
        TransmissionService service = CreateService(client);

        string id = await service.AddTorrent(new Uri("magnet:?xt=urn:btih:abc"));

        await Assert.That(id).IsEqualTo("8");
        await Assert.That(client.AddedUris.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DeleteTorrent_PassesHashAndCleanup()
    {
        FakeTransmissionClient client = new();
        TransmissionService service = CreateService(client);

        await service.DeleteTorrent("hash-1", TorrentFileCleanup.KeepFiles);

        await Assert.That(client.Removed.Count).IsEqualTo(1);
        await Assert.That(client.Removed[0].Id).IsEqualTo("hash-1");
        await Assert.That(client.Removed[0].Cleanup).IsEqualTo(TorrentFileCleanup.KeepFiles);
    }

    [Test]
    public async Task DeleteTorrents_IgnoresBlankIds_AndPassesRest()
    {
        FakeTransmissionClient client = new();
        TransmissionService service = CreateService(client);

        await service.DeleteTorrents(["a", "", "  ", "b"]);

        await Assert.That(client.RemovedBatches.Count).IsEqualTo(1);
        await Assert.That(client.RemovedBatches[0]).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task DeleteTorrents_AllBlank_DoesNotCallClient()
    {
        FakeTransmissionClient client = new();
        TransmissionService service = CreateService(client);

        await service.DeleteTorrents(["", " "]);

        await Assert.That(client.RemovedBatches).IsEmpty();
    }

    [Test]
    public async Task PauseAndResumeTorrent_PassHash()
    {
        FakeTransmissionClient client = new();
        TransmissionService service = CreateService(client);

        await service.PauseTorrent("hash-1");
        await service.ResumeTorrent("hash-2");

        await Assert.That(client.Paused).IsEquivalentTo(["hash-1"]);
        await Assert.That(client.Resumed).IsEquivalentTo(["hash-2"]);
    }

    [Test]
    public async Task PauseAndResumeAll_CallClient()
    {
        FakeTransmissionClient client = new();
        TransmissionService service = CreateService(client);

        await service.PauseTorrents();
        await service.ResumeTorrents();

        await Assert.That(client.PauseAllCalls).IsEqualTo(1);
        await Assert.That(client.ResumeAllCalls).IsEqualTo(1);
    }

    // Stats

    [Test]
    public async Task GetTransferStats_MapsCumulativeStats()
    {
        FakeTransmissionClient client = new()
        {
            Stats = new TransmissionSessionStats
            {
                DownloadSpeed = 111,
                UploadSpeed = 222,
                CumulativeStats = new TransmissionStats { DownloadedBytes = 333, UploadedBytes = 444 }
            }
        };
        TransmissionService service = CreateService(client);

        TorrentTransferStats stats = await service.GetTransferStats();

        await Assert.That(stats.DownloadSpeed).IsEqualTo(111);
        await Assert.That(stats.UploadSpeed).IsEqualTo(222);
        await Assert.That(stats.TotalDownloaded).IsEqualTo(333);
        await Assert.That(stats.TotalUploaded).IsEqualTo(444);
    }

    // Limits

    [Test]
    public async Task ApplyConfiguredLimits_ComputesSpeedAndQueueLimits()
    {
        FakeTransmissionClient client = new()
        {
            Torrents = [Torrent(1, TransmissionStatus.Seeding), Torrent(2, TransmissionStatus.Seeding)]
        };
        TorrentDownloaderConfig config = new()
        {
            Speed = new SpeedConfig { UploadMbps = 8, DownloadMbps = 16 },
            Limits = new TorrentLimitsConfig { MaxActiveDownloads = 3, MaxActiveSeeds = 4 }
        };
        TransmissionService service = CreateService(client, config);

        await service.ApplyConfiguredLimits();

        await Assert.That(client.SessionLimitCalls.Count).IsEqualTo(1);
        (int? Up, bool? UpEnabled, int? Down, bool? DownEnabled, int? MaxDl, int? MaxSeeds) call =
            client.SessionLimitCalls[0];
        await Assert.That(call.Up).IsEqualTo(8 * 1024 / 8);
        await Assert.That(call.UpEnabled).IsTrue();
        await Assert.That(call.Down).IsEqualTo(16 * 1024 / 8);
        await Assert.That(call.DownEnabled).IsTrue();
        await Assert.That(call.MaxDl).IsEqualTo(3);
        await Assert.That(call.MaxSeeds).IsEqualTo(4);
    }

    [Test]
    public async Task ApplyConfiguredLimits_ZeroSpeeds_DisableLimits()
    {
        FakeTransmissionClient client = new();
        TorrentDownloaderConfig config = new()
        {
            Speed = new SpeedConfig { UploadMbps = 0, DownloadMbps = 0 }
        };
        TransmissionService service = CreateService(client, config);

        await service.ApplyConfiguredLimits();

        (int? Up, bool? UpEnabled, int? Down, bool? DownEnabled, int? MaxDl, int? MaxSeeds) call =
            client.SessionLimitCalls[0];
        await Assert.That(call.Up).IsEqualTo(0);
        await Assert.That(call.UpEnabled).IsFalse();
        await Assert.That(call.Down).IsEqualTo(0);
        await Assert.That(call.DownEnabled).IsFalse();
    }

    [Test]
    public async Task ApplyConfiguredLimits_UsesCurrentConfig_WithoutRestart()
    {
        FakeTransmissionClient client = new();
        MutableOptionsMonitor<TorrentDownloaderConfig> monitor = new(new TorrentDownloaderConfig
        {
            Speed = new SpeedConfig { UploadMbps = 1, DownloadMbps = 2 },
            Limits = new TorrentLimitsConfig { MaxActiveDownloads = 1, MaxActiveSeeds = 2 }
        });
        TransmissionService service = CreateService(client, monitor: monitor);

        await service.ApplyConfiguredLimits();
        monitor.Update(new TorrentDownloaderConfig
        {
            Speed = new SpeedConfig { UploadMbps = 9, DownloadMbps = 0 },
            Limits = new TorrentLimitsConfig { MaxActiveDownloads = 7, MaxActiveSeeds = 8 }
        });
        await service.ApplyConfiguredLimits();

        await Assert.That(client.SessionLimitCalls.Count).IsEqualTo(2);
        (int? Up, bool? UpEnabled, int? Down, bool? DownEnabled, int? MaxDl, int? MaxSeeds) second =
            client.SessionLimitCalls[1];
        await Assert.That(second.Up).IsEqualTo(9 * 1024 / 8);
        await Assert.That(second.UpEnabled).IsTrue();
        await Assert.That(second.DownEnabled).IsFalse();
        await Assert.That(second.MaxDl).IsEqualTo(7);
        await Assert.That(second.MaxSeeds).IsEqualTo(8);
    }

    // Events

    [Test]
    public async Task NotifyTorrentsUpdated_RaisesEvent()
    {
        FakeTransmissionClient client = new();
        TransmissionService service = CreateService(client);
        TorrentsChangedEventArgs? received = null;
        service.TorrentsUpdated += (_, e) => received = e;

        List<TorrentItem> items = [new() { Hash = "h", Name = "n" }];
        service.NotifyTorrentsUpdated(items);

        await Assert.That(received).IsNotNull();
        await Assert.That(received!.Torrents.Count).IsEqualTo(1);
    }

    [Test]
    public async Task NotifyTorrentStateChanged_RaisesEvent()
    {
        FakeTransmissionClient client = new();
        TransmissionService service = CreateService(client);
        TorrentStateChangedEventArgs? received = null;
        service.TorrentStateChanged += (_, e) => received = e;

        TorrentItem item = new() { Hash = "h", Name = "n" };
        service.NotifyTorrentStateChanged(item);

        await Assert.That(received).IsNotNull();
        await Assert.That(received!.Torrent.Hash).IsEqualTo("h");
    }
}
