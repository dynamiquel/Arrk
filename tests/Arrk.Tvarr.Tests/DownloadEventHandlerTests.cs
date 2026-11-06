using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Core.Events;
using Arrk.TestSupport;
using Arrk.Tvarr.Configurations;
using Arrk.Tvarr.Models;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Tests;

public class DownloadEventHandlerTests
{
    sealed class Harness : IDisposable
    {
        readonly string _dbPath;
        readonly ServiceProvider _provider;

        public EventBus Bus { get; }
        public DownloadEventHandler Handler { get; }
        public MutableOptionsMonitor<TvConfig> Config { get; }

        public Harness()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"stackarr-evthandler-{Guid.NewGuid():N}.db");
            ServiceCollection services = new();
            services.AddLogging();
            services.AddSingleton<IEventBus, EventBus>();
            services.AddSingleton<IModuleEntityConfiguration, PendingTorrentCleanupConfiguration>();
            services.AddScoped(_ => new ArrkDbContext(TestDb.Options(_dbPath), TestDb.Configurations));

            _provider = services.BuildServiceProvider();
            Scope(db => db.Database.EnsureCreated());

            Bus = (EventBus)_provider.GetRequiredService<IEventBus>();
            Config = new(new TvConfig());
            Handler = new(
                Bus,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Config,
                NullLogger<DownloadEventHandler>.Instance);
        }

        public void Scope(Action<ArrkDbContext> action)
        {
            using IServiceScope scope = _provider.CreateScope();
            action(scope.ServiceProvider.GetRequiredService<ArrkDbContext>());
        }

        public async Task<List<PendingTorrentCleanup>> Rows()
        {
            using IServiceScope scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ArrkDbContext>();
            return await db.Set<PendingTorrentCleanup>().AsNoTracking().ToListAsync();
        }

        public void Dispose()
        {
            Handler.Dispose();
            _provider.Dispose();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
    }

    [Test]
    public async Task TorrentDeleted_CreatesPendingRowsForSonarrAndRadarr()
    {
        using Harness harness = new();

        await harness.Bus.Publish(new TorrentDeletedEvent("hash1", "Torrent", TorrentDeleteReason.Error, DateTime.UtcNow));

        List<PendingTorrentCleanup> rows = await harness.Rows();
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows.Select(r => r.TargetService)).IsEquivalentTo(["Sonarr", "Radarr"]);
        await Assert.That(rows.All(r => r.TorrentHash == "hash1")).IsTrue();
        await Assert.That(rows.All(r => r.AttemptCount == 0 && r.CompletedAt is null)).IsTrue();
    }

    [Test]
    public async Task TorrentDeleted_RepeatedWhilePending_DoesNotDuplicate()
    {
        using Harness harness = new();

        await harness.Bus.Publish(new TorrentDeletedEvent("hash1", "T", TorrentDeleteReason.Error, DateTime.UtcNow));
        await harness.Bus.Publish(new TorrentDeletedEvent("hash1", "T", TorrentDeleteReason.Slow, DateTime.UtcNow));

        await Assert.That((await harness.Rows()).Count).IsEqualTo(2);
    }

    [Test]
    public async Task TorrentDeleted_AfterCompletion_ReArmsRow()
    {
        using Harness harness = new();

        await harness.Bus.Publish(new TorrentDeletedEvent("hash1", "T", TorrentDeleteReason.Error, DateTime.UtcNow));

        harness.Scope(db =>
        {
            foreach (PendingTorrentCleanup row in db.Set<PendingTorrentCleanup>())
            {
                row.CompletedAt = DateTime.UtcNow;
                row.AttemptCount = 3;
                row.LastError = "old";
            }
            db.SaveChanges();
        });

        await harness.Bus.Publish(new TorrentDeletedEvent("hash1", "T", TorrentDeleteReason.Storage, DateTime.UtcNow));

        List<PendingTorrentCleanup> rows = await harness.Rows();
        await Assert.That(rows.All(r => r.CompletedAt is null)).IsTrue();
        await Assert.That(rows.All(r => r.AttemptCount == 0)).IsTrue();
        await Assert.That(rows.All(r => r.LastError is null)).IsTrue();
        await Assert.That(rows.All(r => r.Reason == TorrentDeleteReason.Storage)).IsTrue();
    }

    [Test]
    public async Task Dispose_Unsubscribes()
    {
        using Harness harness = new();
        harness.Handler.Dispose();

        await harness.Bus.Publish(new TorrentDeletedEvent("hash1", "T", TorrentDeleteReason.Error, DateTime.UtcNow));

        await Assert.That(await harness.Rows()).IsEmpty();
    }

    [Test]
    public async Task StartStop_AreNoOps()
    {
        using Harness harness = new();

        await Assert.That(async () =>
        {
            await harness.Handler.StartAsync(CancellationToken.None);
            await harness.Handler.StopAsync(CancellationToken.None);
        }).ThrowsNothing();
    }

    [Test]
    public async Task TorrentDeleted_TvCategory_QueuesOnlySonarr()
    {
        using Harness harness = new();

        await harness.Bus.Publish(new TorrentDeletedEvent(
            "hash1", "Show.S01E01", TorrentDeleteReason.Stalled, DateTime.UtcNow, "tv"));

        List<PendingTorrentCleanup> rows = await harness.Rows();
        await Assert.That(rows.Select(r => r.TargetService)).IsEquivalentTo(["Sonarr"]);
    }

    [Test]
    public async Task TorrentDeleted_MovieCategory_QueuesOnlyRadarr()
    {
        using Harness harness = new();

        await harness.Bus.Publish(new TorrentDeletedEvent(
            "hash1", "Movie.2014", TorrentDeleteReason.Error, DateTime.UtcNow, "movies"));

        List<PendingTorrentCleanup> rows = await harness.Rows();
        await Assert.That(rows.Select(r => r.TargetService)).IsEquivalentTo(["Radarr"]);
    }

    [Test]
    public async Task TorrentDeleted_MusicLabel_QueuesNothing()
    {
        using Harness harness = new();

        await harness.Bus.Publish(new TorrentDeletedEvent(
            "hash1", "Album [FLAC]", TorrentDeleteReason.Slow, DateTime.UtcNow,
            "stackarr-music", ["stackarr-music", "stackarr-album-1856"]));

        await Assert.That(await harness.Rows()).IsEmpty();
    }

    [Test]
    public async Task TorrentDeleted_ErrorWithRetriggerDisabled_QueuesNothing()
    {
        using Harness harness = new();
        harness.Config.Update(new TvConfig
        {
            FailedDownloads = new TvFailedDownloadConfig { RetriggerSearch = false }
        });

        await harness.Bus.Publish(new TorrentDeletedEvent(
            "hash1", "Show.S01E01", TorrentDeleteReason.Error, DateTime.UtcNow, "tv"));

        await Assert.That(await harness.Rows()).IsEmpty();
    }

    [Test]
    public async Task TorrentDeleted_StalledWithRetriggerDisabled_StillQueues()
    {
        using Harness harness = new();
        harness.Config.Update(new TvConfig
        {
            FailedDownloads = new TvFailedDownloadConfig { RetriggerSearch = false }
        });

        await harness.Bus.Publish(new TorrentDeletedEvent(
            "hash1", "Show.S01E01", TorrentDeleteReason.Stalled, DateTime.UtcNow, "tv"));

        await Assert.That((await harness.Rows()).Select(r => r.TargetService)).IsEquivalentTo(["Sonarr"]);
    }
}
