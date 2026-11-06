using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Notifications;
using Arrk.Speedtest;
using Arrk.Speedtest.Jobs;
using Arrk.Speedtest.Models;
using Arrk.TestSupport;

namespace Arrk.Speedtest.Tests;

/// <summary>
/// Runs the REAL <see cref="CloudflareSpeedtestClient"/> against a REAL HTTP server (an nginx
/// container configured to emulate the Cloudflare speed endpoints) so request building, timing,
/// header parsing and result aggregation are exercised end-to-end.
/// Skipped automatically when Docker is unavailable.
/// </summary>
[NotInParallel(SpeedtestServerShared.ParallelKey)]
public class SpeedtestClientIntegrationTests
{
    static Task<SpeedtestServer?> GetServerAsync() => SpeedtestServerShared.GetServerAsync();

    [Test]
    public async Task RunTest_AgainstRealServer_ProducesMeasurements()
    {
        SpeedtestServer? server = await GetServerAsync();
        if (server is null) return;

        CloudflareSpeedtestClient client = server.CreateClient();
        DateTime before = DateTime.UtcNow;

        CloudflareSpeedtestResult result = await client.RunTestAsync();

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.DownloadMbps).IsGreaterThanOrEqualTo(0);
        await Assert.That(result.UploadMbps).IsGreaterThanOrEqualTo(0);
        await Assert.That(result.PingMs).IsGreaterThanOrEqualTo(0);
        await Assert.That(result.JitterMs).IsGreaterThanOrEqualTo(0);
        await Assert.That(result.TestedAt).IsGreaterThanOrEqualTo(before.AddSeconds(-1));
    }

    [Test]
    public async Task RunTest_AgainstUnreachableServer_ReturnsErrorResult()
    {
        int deadPort = DockerTestContainer.GetAvailablePort();
        CloudflareSpeedtestClient client = new(
            new HttpClient { Timeout = TimeSpan.FromSeconds(2) },
            NullLogger<CloudflareSpeedtestClient>.Instance)
        {
            DownloadUrl = $"http://localhost:{deadPort}/__down",
            UploadUrl = $"http://localhost:{deadPort}/__up",
            Profile = SpeedtestProfile.Fast
        };

        CloudflareSpeedtestResult result = await client.RunTestAsync();

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.ErrorMessage).IsNotNull();
    }

    [Test]
    public async Task RunTest_Concurrent_AllComplete()
    {
        SpeedtestServer? server = await GetServerAsync();
        if (server is null) return;

        CloudflareSpeedtestClient client = server.CreateClient();
        IEnumerable<Task<CloudflareSpeedtestResult>> tasks =
            Enumerable.Range(0, 4).Select(_ => client.RunTestAsync());

        CloudflareSpeedtestResult[] results = await Task.WhenAll(tasks);

        await Assert.That(results.Length).IsEqualTo(4);
        await Assert.That(results.All(r => !r.IsError)).IsTrue();
    }

    [Test]
    public async Task RunTest_Cancellation_ReturnsGracefullyWithoutMeasurements()
    {
        SpeedtestServer? server = await GetServerAsync();
        if (server is null) return;

        CloudflareSpeedtestClient client = server.CreateClient();
        // A token that is already cancelled stops the run at the first cancellation check. The
        // client intentionally degrades to a zeroed, non-error result rather than throwing.
        using CancellationTokenSource cts = new();
        cts.Cancel();

        CloudflareSpeedtestResult result = await client.RunTestAsync(cts.Token);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.DownloadMbps).IsEqualTo(0);
        await Assert.That(result.UploadMbps).IsEqualTo(0);
        await Assert.That(result.PingMs).IsEqualTo(0);
    }

    [Test]
    public async Task SpeedtestJob_Enabled_PersistsResult()
    {
        SpeedtestServer? server = await GetServerAsync();
        if (server is null) return;

        string dbPath = Path.Combine(Path.GetTempPath(), $"stackarr-speedtest-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ArrkDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            await using (ArrkDbContext setup = new(options, []))
            {
                await setup.Database.EnsureCreatedAsync();
            }

            TestDbContextFactory factory = new(options);
            SpeedtestJob job = new(
                server.CreateClient(),
                factory,
                new MutableOptionsMonitor<SpeedtestConfig>(new SpeedtestConfig { Enabled = true }),
                new FakeEventStore(),
                NullLogger<SpeedtestJob>.Instance);

            await job.Execute(CancellationToken.None);

            await using ArrkDbContext verify = new(options, []);
            List<SpeedtestResult> rows = await verify.SpeedtestResults.ToListAsync();
            await Assert.That(rows.Count).IsEqualTo(1);
            await Assert.That(rows[0].Target).IsEqualTo("network");
            await Assert.That(rows[0].ErrorMessage).IsNull();
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SpeedtestJob_BelowThreshold_EmitsDegradedNotification()
    {
        SpeedtestServer? server = await GetServerAsync();
        if (server is null) return;

        string dbPath = Path.Combine(Path.GetTempPath(), $"stackarr-speedtest-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ArrkDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            await using (ArrkDbContext setup = new(options, []))
            {
                await setup.Database.EnsureCreatedAsync();
            }

            FakeEventStore events = new();
            SpeedtestJob job = new(
                server.CreateClient(),
                new TestDbContextFactory(options),
                new MutableOptionsMonitor<SpeedtestConfig>(new SpeedtestConfig
                {
                    Enabled = true,
                    MinDownloadMbps = double.MaxValue
                }),
                events,
                NullLogger<SpeedtestJob>.Instance);

            await job.Execute(CancellationToken.None);

            await Assert.That(events.Notifications.Any(n => n.Key == SpeedtestNotificationTopics.SpeedtestDegraded)).IsTrue();
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SpeedtestJob_AboveThreshold_EmitsCompletedButNotDegraded()
    {
        SpeedtestServer? server = await GetServerAsync();
        if (server is null) return;

        string dbPath = Path.Combine(Path.GetTempPath(), $"stackarr-speedtest-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ArrkDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            await using (ArrkDbContext setup = new(options, []))
            {
                await setup.Database.EnsureCreatedAsync();
            }

            FakeEventStore events = new();
            SpeedtestJob job = new(
                server.CreateClient(),
                new TestDbContextFactory(options),
                new MutableOptionsMonitor<SpeedtestConfig>(new SpeedtestConfig
                {
                    Enabled = true,
                    MinDownloadMbps = 0
                }),
                events,
                NullLogger<SpeedtestJob>.Instance);

            await job.Execute(CancellationToken.None);

            await Assert.That(events.Notifications.Any(n => n.Key == SpeedtestNotificationTopics.SpeedtestCompleted)).IsTrue();
            await Assert.That(events.Notifications.Any(n => n.Key == SpeedtestNotificationTopics.SpeedtestDegraded)).IsFalse();
            await Assert.That(events.Notifications.Any(n => n.Key == SpeedtestNotificationTopics.SpeedtestRecovered)).IsFalse();
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SpeedtestJob_RecoversFromDegraded_EmitsRecoveredNotification()
    {
        SpeedtestServer? server = await GetServerAsync();
        if (server is null) return;

        string dbPath = Path.Combine(Path.GetTempPath(), $"stackarr-speedtest-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ArrkDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            await using (ArrkDbContext setup = new(options, []))
            {
                await setup.Database.EnsureCreatedAsync();
            }

            FakeEventStore events = new();
            // Seed a previous degradation so the job can detect recovery.
            await events.RecordAsync(new SystemEventRecord
            {
                Category = EventCategory.Health,
                EventType = "speedtest.degraded",
                Severity = EventSeverity.Warning,
                Message = "previous degradation",
                Timestamp = DateTime.UtcNow.AddHours(-1)
            });

            SpeedtestJob job = new(
                server.CreateClient(),
                new TestDbContextFactory(options),
                new MutableOptionsMonitor<SpeedtestConfig>(new SpeedtestConfig
                {
                    Enabled = true,
                    MinDownloadMbps = 0
                }),
                events,
                NullLogger<SpeedtestJob>.Instance);

            await job.Execute(CancellationToken.None);

            await Assert.That(events.Notifications.Any(n => n.Key == SpeedtestNotificationTopics.SpeedtestRecovered)).IsTrue();
            await Assert.That(events.Notifications.Any(n => n.Key == SpeedtestNotificationTopics.SpeedtestDegraded)).IsFalse();
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SpeedtestJob_Disabled_DoesNotPersist()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"stackarr-speedtest-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ArrkDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            await using (ArrkDbContext setup = new(options, []))
            {
                await setup.Database.EnsureCreatedAsync();
            }

            // A client pointed at a dead port proves the job short-circuits before testing.
            int deadPort = DockerTestContainer.GetAvailablePort();
            CloudflareSpeedtestClient client = new(
                new HttpClient { Timeout = TimeSpan.FromSeconds(2) },
                NullLogger<CloudflareSpeedtestClient>.Instance)
            {
                DownloadUrl = $"http://localhost:{deadPort}/__down",
                UploadUrl = $"http://localhost:{deadPort}/__up",
                Profile = SpeedtestProfile.Fast
            };

            SpeedtestJob job = new(
                client,
                new TestDbContextFactory(options),
                new MutableOptionsMonitor<SpeedtestConfig>(new SpeedtestConfig { Enabled = false }),
                new FakeEventStore(),
                NullLogger<SpeedtestJob>.Instance);

            await job.Execute(CancellationToken.None);

            await using ArrkDbContext verify = new(options, []);
            await Assert.That(await verify.SpeedtestResults.ToListAsync()).IsEmpty();
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    sealed class TestDbContextFactory(DbContextOptions<ArrkDbContext> options)
        : IDbContextFactory<ArrkDbContext>
    {
        public ArrkDbContext CreateDbContext() => new(options, []);
        public ValueTask<ArrkDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ArrkDbContext(options, []));
    }

}
