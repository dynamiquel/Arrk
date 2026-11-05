using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Enums;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.TestSupport;

namespace Stackarr.Transmission.Tests;

public class TransmissionLimitsJobTests
{
    static (TransmissionLimitsJob Job, ServiceRegistry Registry, FakeTransmissionClient Client) Create(
        bool healthy,
        bool addService = true,
        ITorrentDownloader? downloader = null)
    {
        ServiceRegistry registry = new();
        if (addService)
        {
            Service service = new(new TransmissionServiceDefinition());
            service.State.AggregateState = healthy ? ContainerState.Running : ContainerState.Stopped;
            registry.Register(service);
        }

        FakeTransmissionClient client = new();
        ITorrentDownloader torrentDownloader = downloader
            ?? new TransmissionService(client, new MutableOptionsMonitor<TorrentDownloaderConfig>(new TorrentDownloaderConfig()),
                NullLogger<TransmissionService>.Instance);

        return (new TransmissionLimitsJob(registry, torrentDownloader), registry, client);
    }

    [Test]
    public async Task Execute_WhenServiceHealthy_AppliesLimits()
    {
        (TransmissionLimitsJob job, _, FakeTransmissionClient client) = Create(healthy: true);

        await job.Execute(CancellationToken.None);

        await Assert.That(client.SessionLimitCalls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Execute_WhenServiceUnhealthy_DoesNotApplyLimits()
    {
        (TransmissionLimitsJob job, _, FakeTransmissionClient client) = Create(healthy: false);

        await job.Execute(CancellationToken.None);

        await Assert.That(client.SessionLimitCalls).IsEmpty();
    }

    [Test]
    public async Task Execute_WhenServiceMissing_DoesNotApplyLimits()
    {
        (TransmissionLimitsJob job, _, FakeTransmissionClient client) = Create(healthy: true, addService: false);

        await job.Execute(CancellationToken.None);

        await Assert.That(client.SessionLimitCalls).IsEmpty();
    }

    [Test]
    public async Task Execute_WhenDownloaderIsNotTransmissionService_DoesNothing()
    {
        StubTorrentDownloader stub = new();
        (TransmissionLimitsJob job, _, _) = Create(healthy: true, downloader: stub);

        await job.Execute(CancellationToken.None);

        await Assert.That(stub.DeleteCalls).IsEqualTo(0);
    }

    sealed class StubTorrentDownloader : ITorrentDownloader
    {
        public int DeleteCalls { get; private set; }

        public Task<TorrentItem?> GetTorrent(string id, CancellationToken ct = default) => Task.FromResult<TorrentItem?>(null);
        public Task<string> AddTorrent(byte[] torrentData, string category = "", string savePath = "", CancellationToken ct = default) => Task.FromResult("");
        public Task<string> AddTorrent(Uri torrentUri, string category = "", string savePath = "", CancellationToken ct = default) => Task.FromResult("");
        public Task DeleteTorrent(string id, TorrentFileCleanup fileCleanup = TorrentFileCleanup.DeleteFiles, CancellationToken ct = default)
        {
            DeleteCalls++;
            return Task.CompletedTask;
        }
        public Task PauseTorrent(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeTorrent(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TorrentTransferStats> GetTransferStats(CancellationToken ct = default) => Task.FromResult(new TorrentTransferStats());
        public Task<List<TorrentItem>> GetTorrents(string filter = "all", CancellationToken ct = default) => Task.FromResult(new List<TorrentItem>());
        public Task PauseTorrents(string filter = "all", CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeTorrents(string filter = "all", CancellationToken ct = default) => Task.CompletedTask;
    }
}
