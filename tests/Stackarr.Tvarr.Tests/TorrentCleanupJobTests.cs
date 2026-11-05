using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core;
using Stackarr.Core.Events;
using Stackarr.TestSupport;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Configurations;
using Stackarr.Tvarr.Models;
using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr.Tests;

/// <summary>
/// The job drains <see cref="PendingTorrentCleanup"/> rows written by <see cref="DownloadEventHandler"/>.
/// It must resolve each row against the arr, and must stop retrying (and complete the row) once the
/// attempt budget is spent instead of pinning it forever.
/// </summary>
public class TorrentCleanupJobTests
{
    sealed class Harness : IDisposable
    {
        readonly string _dbPath;
        readonly ServiceProvider _provider;

        public FakeHttpMessageHandler SonarrHandler { get; }
        public FakeHttpMessageHandler RadarrHandler { get; }
        public TorrentCleanupJob Job { get; }

        public Harness()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"stackarr-cleanupjob-{Guid.NewGuid():N}.db");
            SonarrHandler = new()
            {
                Fallback = (_, _) => throw new InvalidOperationException("Sonarr should not be called")
            };
            RadarrHandler = new()
            {
                Fallback = (_, _) => throw new InvalidOperationException("Radarr should not be called")
            };

            ServiceCollection services = new();
            services.AddLogging();
            services.AddSingleton<IModuleEntityConfiguration, PendingTorrentCleanupConfiguration>();
            services.AddScoped(_ => new StackarrDbContext(TestDb.Options(_dbPath), TestDb.Configurations));
            services.AddSingleton(new SonarrService(
                new SonarrClient("http://localhost", "key", SonarrHandler),
                NullLogger<SonarrService>.Instance));
            services.AddSingleton(new RadarrService(
                new RadarrClient("http://localhost", "key", RadarrHandler),
                NullLogger<RadarrService>.Instance));

            _provider = services.BuildServiceProvider();
            using (IServiceScope scope = _provider.CreateScope())
                scope.ServiceProvider.GetRequiredService<StackarrDbContext>().Database.EnsureCreated();

            Job = new(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<TorrentCleanupJob>.Instance);
        }

        public void Add(PendingTorrentCleanup row)
        {
            using IServiceScope scope = _provider.CreateScope();
            StackarrDbContext db = scope.ServiceProvider.GetRequiredService<StackarrDbContext>();
            db.Set<PendingTorrentCleanup>().Add(row);
            db.SaveChanges();
        }

        public async Task<List<PendingTorrentCleanup>> Rows()
        {
            using IServiceScope scope = _provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<StackarrDbContext>()
                .Set<PendingTorrentCleanup>().AsNoTracking().ToListAsync();
        }

        public void Dispose()
        {
            _provider.Dispose();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
    }

    static PendingTorrentCleanup Row(string hash, string target, int attempts = 0) => new()
    {
        TorrentHash = hash,
        TorrentName = "Release",
        Reason = TorrentDeleteReason.Error,
        TargetService = target,
        AttemptCount = attempts,
        CreatedAt = DateTime.UtcNow
    };

    [Test]
    public async Task ExhaustedRow_IsCompletedWithoutCallingAnArr()
    {
        using Harness harness = new();
        harness.Add(Row("hash1", "Sonarr", attempts: TorrentCleanupJob.MaxAttempts));

        await harness.Job.Execute();

        PendingTorrentCleanup row = (await harness.Rows()).Single();
        await Assert.That(row.CompletedAt).IsNotNull();
        await Assert.That(row.LastError!).Contains("Gave up");
        await Assert.That(harness.SonarrHandler.Requests).IsEmpty();
        await Assert.That(harness.RadarrHandler.Requests).IsEmpty();
    }

    [Test]
    public async Task PendingRow_IsResolvedThroughItsTargetArr()
    {
        using Harness harness = new();
        harness.Add(Row("aaaabbbb", "Sonarr"));
        harness.SonarrHandler.Fallback = (request, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
            (request.Method, request.RequestUri!.AbsolutePath) switch
            {
                (_, "/api/v3/queue") => """{ "records": [] }""",
                (_, "/api/v3/history") =>
                    """{ "records": [ { "id": 21, "eventType": "grabbed", "downloadId": "aaaabbbb", "episodeId": 634 } ] }""",
                _ => "{}"
            }));

        await harness.Job.Execute();

        PendingTorrentCleanup row = (await harness.Rows()).Single();
        await Assert.That(row.CompletedAt).IsNotNull();
        await Assert.That(row.LastError).IsNull();
        await Assert.That(harness.SonarrHandler.Requests.Any(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/v3/command")).IsTrue();
        await Assert.That(harness.RadarrHandler.Requests).IsEmpty();
    }

    [Test]
    public async Task UnknownTarget_IsCompletedWithoutNetworkCalls()
    {
        using Harness harness = new();
        harness.Add(Row("hash1", "Lidarr"));

        await harness.Job.Execute();

        await Assert.That((await harness.Rows()).Single().CompletedAt).IsNotNull();
        await Assert.That(harness.SonarrHandler.Requests).IsEmpty();
        await Assert.That(harness.RadarrHandler.Requests).IsEmpty();
    }
}
