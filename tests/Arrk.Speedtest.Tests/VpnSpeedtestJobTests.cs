using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Speedtest.Jobs;
using Arrk.Speedtest.Models;
using Arrk.TestSupport;

namespace Arrk.Speedtest.Tests;

public class VpnSpeedtestJobTests
{
    static ArrkInstance Instance() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = "demo" })
        .Build());

    static IConfiguration VpnConfig(bool enabled = true, bool torrentOptIn = false) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["vpn:enabled"] = enabled.ToString().ToLowerInvariant(),
                ["services:torrentDownloader:vpn"] = torrentOptIn.ToString().ToLowerInvariant()
            })
            .Build();

    /// <summary>
    /// Serves the Cloudflare speedtest endpoints. An optional per-direction delay makes the
    /// measured bandwidth deterministic and low (256-byte payload / delay ≪ 1 Mbps). The job
    /// runs its test over the proxy client, so this handler backs <c>vpnHttp</c>.
    /// </summary>
    static FakeHttpMessageHandler SpeedtestHandler(
        TimeSpan? downloadDelay = null,
        TimeSpan? uploadDelay = null)
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = async (request, ct) =>
        {
            bool isLatency = request.RequestUri!.Query.Contains("bytes=0");
            if (request.Method == HttpMethod.Post && uploadDelay is { } upload)
                await Task.Delay(upload, ct);
            else if (request.Method == HttpMethod.Get && !isLatency && downloadDelay is { } download)
                await Task.Delay(download, ct);

            return FakeHttpMessageHandler.JsonResponse("ok");
        };
        return handler;
    }

    static CloudflareSpeedtestClient Client() =>
        new(new HttpClient(SpeedtestHandler()), NullLogger<CloudflareSpeedtestClient>.Instance)
        {
            Profile = SpeedtestProfile.Fast
        };

    static (VpnSpeedtestJob Job, TestDbContextFactory Factory, FakeEventStore Events, StubGluetunControlClient Control) CreateJob(
        IConfiguration configuration,
        SpeedtestConfig config,
        HttpClient vpnHttp,
        VpnConfig? vpnConfig = null)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"stackarr-vpn-speedtest-{Guid.NewGuid():N}.db");
        DbContextOptions<ArrkDbContext> options = new DbContextOptionsBuilder<ArrkDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        using (ArrkDbContext setup = new(options, []))
        {
            setup.Database.EnsureCreated();
        }

        FakeEventStore events = new();
        TestDbContextFactory factory = new(options, dbPath);
        StubGluetunControlClient control = new(Instance());
        VpnSpeedtestJob job = new(
            Client(),
            new StubVpnSpeedtestClient(Instance(), vpnHttp),
            factory,
            new MutableOptionsMonitor<SpeedtestConfig>(config),
            new MutableOptionsMonitor<VpnConfig>(vpnConfig ?? new VpnConfig()),
            control,
            configuration,
            events,
            NullLogger<VpnSpeedtestJob>.Instance);

        return (job, factory, events, control);
    }

    static TimeSpan LowBandwidth => TimeSpan.FromMilliseconds(150);

    [Test]
    public async Task Execute_WhenVpnRequiredAndVpnOptedIn_StoresVpnResult()
    {
        using HttpClient vpnHttp = new(SpeedtestHandler());
        (VpnSpeedtestJob job, TestDbContextFactory factory, _, _) = CreateJob(
            VpnConfig(enabled: false, torrentOptIn: true),
            new SpeedtestConfig { Enabled = true },
            vpnHttp);

        await job.Execute(CancellationToken.None);

        await using ArrkDbContext db = factory.CreateDbContext();
        List<SpeedtestResult> rows = await db.SpeedtestResults.ToListAsync();
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Target).IsEqualTo(VpnSpeedtestJob.Target);
        await Assert.That(rows[0].ErrorMessage).IsNull();

        Cleanup(factory);
    }

    [Test]
    public async Task Execute_WhenVpnNotRequired_DoesNothing()
    {
        using HttpClient vpnHttp = new(SpeedtestHandler());
        (VpnSpeedtestJob job, TestDbContextFactory factory, _, _) = CreateJob(
            VpnConfig(enabled: false, torrentOptIn: false),
            new SpeedtestConfig { Enabled = true },
            vpnHttp);

        await job.Execute(CancellationToken.None);

        await using ArrkDbContext db = factory.CreateDbContext();
        await Assert.That(await db.SpeedtestResults.ToListAsync()).IsEmpty();

        Cleanup(factory);
    }

    [Test]
    public async Task Execute_WhenSpeedtestDisabled_DoesNothing()
    {
        using HttpClient vpnHttp = new(SpeedtestHandler());
        (VpnSpeedtestJob job, TestDbContextFactory factory, _, _) = CreateJob(
            VpnConfig(enabled: true),
            new SpeedtestConfig { Enabled = false },
            vpnHttp);

        await job.Execute(CancellationToken.None);

        await using ArrkDbContext db = factory.CreateDbContext();
        await Assert.That(await db.SpeedtestResults.ToListAsync()).IsEmpty();

        Cleanup(factory);
    }

    [Test]
    public async Task Execute_BelowThreshold_EmitsVpnDegradedNotification()
    {
        using HttpClient vpnHttp = new(SpeedtestHandler());
        (VpnSpeedtestJob job, TestDbContextFactory factory, FakeEventStore events, _) = CreateJob(
            VpnConfig(enabled: true),
            new SpeedtestConfig { Enabled = true, MinDownloadMbps = double.MaxValue },
            vpnHttp);

        await job.Execute(CancellationToken.None);

        await Assert.That(events.Notifications.Any(n => n.Key == SpeedtestNotificationTopics.VpnSpeedtestDegraded)).IsTrue();

        Cleanup(factory);
    }

    [Test]
    public async Task Execute_DegradedAndBelowDownloadThreshold_RestartsTunnel()
    {
        using HttpClient vpnHttp = new(SpeedtestHandler(downloadDelay: LowBandwidth));
        (VpnSpeedtestJob job, TestDbContextFactory factory, FakeEventStore events, StubGluetunControlClient control) = CreateJob(
            VpnConfig(enabled: true),
            new SpeedtestConfig { Enabled = true, MinDownloadMbps = 100 },
            vpnHttp,
            vpnConfig: new VpnConfig { Restart = new VpnRestartConfig { DownloadThreshold = 5, UploadThreshold = 0 } });

        await job.Execute(CancellationToken.None);

        await Assert.That(control.RestartCount).IsEqualTo(1);
        await Assert.That(events.Notifications.Any(n => n.Key == SpeedtestNotificationTopics.VpnRestarted)).IsTrue();

        Cleanup(factory);
    }

    [Test]
    public async Task Execute_DegradedAndBelowUploadThresholdOnly_RestartsTunnel()
    {
        using HttpClient vpnHttp = new(SpeedtestHandler(uploadDelay: LowBandwidth));
        (VpnSpeedtestJob job, TestDbContextFactory factory, _, StubGluetunControlClient control) = CreateJob(
            VpnConfig(enabled: true),
            new SpeedtestConfig { Enabled = true, MinUploadMbps = 100 },
            vpnHttp,
            vpnConfig: new VpnConfig { Restart = new VpnRestartConfig { DownloadThreshold = 0, UploadThreshold = 5 } });

        await job.Execute(CancellationToken.None);

        await Assert.That(control.RestartCount).IsEqualTo(1);

        Cleanup(factory);
    }

    [Test]
    public async Task Execute_BelowUploadThresholdButDirectionDisabled_DoesNotRestart()
    {
        using HttpClient vpnHttp = new(SpeedtestHandler(uploadDelay: LowBandwidth));
        (VpnSpeedtestJob job, TestDbContextFactory factory, _, StubGluetunControlClient control) = CreateJob(
            VpnConfig(enabled: true),
            new SpeedtestConfig { Enabled = true, MinUploadMbps = 100 },
            vpnHttp,
            vpnConfig: new VpnConfig { Restart = new VpnRestartConfig { DownloadThreshold = 0, UploadThreshold = 0 } });

        await job.Execute(CancellationToken.None);

        await Assert.That(control.RestartCount).IsEqualTo(0);

        Cleanup(factory);
    }

    [Test]
    public async Task Execute_DegradedButAboveThresholds_DoesNotRestart()
    {
        using HttpClient vpnHttp = new(SpeedtestHandler(downloadDelay: LowBandwidth));
        (VpnSpeedtestJob job, TestDbContextFactory factory, _, StubGluetunControlClient control) = CreateJob(
            VpnConfig(enabled: true),
            new SpeedtestConfig { Enabled = true, MinDownloadMbps = 100 },
            vpnHttp,
            vpnConfig: new VpnConfig { Restart = new VpnRestartConfig { DownloadThreshold = 1e-6, UploadThreshold = 1e-6 } });

        await job.Execute(CancellationToken.None);

        await Assert.That(control.RestartCount).IsEqualTo(0);

        Cleanup(factory);
    }

    [Test]
    public async Task Execute_BelowThresholdButNotDegraded_DoesNotRestart()
    {
        using HttpClient vpnHttp = new(SpeedtestHandler(downloadDelay: LowBandwidth));
        (VpnSpeedtestJob job, TestDbContextFactory factory, _, StubGluetunControlClient control) = CreateJob(
            VpnConfig(enabled: true),
            new SpeedtestConfig { Enabled = true },
            vpnHttp,
            vpnConfig: new VpnConfig { Restart = new VpnRestartConfig { DownloadThreshold = 5, UploadThreshold = 0 } });

        await job.Execute(CancellationToken.None);

        await Assert.That(control.RestartCount).IsEqualTo(0);

        Cleanup(factory);
    }

    [Test]
    public async Task Execute_RestartDisabled_DoesNotRestart()
    {
        using HttpClient vpnHttp = new(SpeedtestHandler(downloadDelay: LowBandwidth));
        (VpnSpeedtestJob job, TestDbContextFactory factory, _, StubGluetunControlClient control) = CreateJob(
            VpnConfig(enabled: true),
            new SpeedtestConfig { Enabled = true, MinDownloadMbps = 100 },
            vpnHttp,
            vpnConfig: new VpnConfig { Restart = new VpnRestartConfig { Enabled = false } });

        await job.Execute(CancellationToken.None);

        await Assert.That(control.RestartCount).IsEqualTo(0);

        Cleanup(factory);
    }

    [Test]
    public async Task Execute_WithinCooldown_DoesNotRestart()
    {
        using HttpClient vpnHttp = new(SpeedtestHandler(downloadDelay: LowBandwidth));
        (VpnSpeedtestJob job, TestDbContextFactory factory, FakeEventStore events, StubGluetunControlClient control) = CreateJob(
            VpnConfig(enabled: true),
            new SpeedtestConfig { Enabled = true, MinDownloadMbps = 100 },
            vpnHttp,
            vpnConfig: new VpnConfig { Restart = new VpnRestartConfig { DownloadThreshold = 5, UploadThreshold = 0, CooldownMinutes = 60 } });

        await events.RecordAsync(new SystemEventRecord
        {
            Category = EventCategory.Health,
            EventType = SpeedtestNotificationTopics.VpnRestarted,
            Severity = EventSeverity.Warning,
            Message = "previous restart",
            Timestamp = DateTime.UtcNow.AddMinutes(-5)
        });

        await job.Execute(CancellationToken.None);

        await Assert.That(control.RestartCount).IsEqualTo(0);

        Cleanup(factory);
    }

    [Test]
    public async Task Execute_AfterCooldownElapsed_RestartsTunnel()
    {
        using HttpClient vpnHttp = new(SpeedtestHandler(downloadDelay: LowBandwidth));
        (VpnSpeedtestJob job, TestDbContextFactory factory, FakeEventStore events, StubGluetunControlClient control) = CreateJob(
            VpnConfig(enabled: true),
            new SpeedtestConfig { Enabled = true, MinDownloadMbps = 100 },
            vpnHttp,
            vpnConfig: new VpnConfig { Restart = new VpnRestartConfig { DownloadThreshold = 5, UploadThreshold = 0, CooldownMinutes = 60 } });

        await events.RecordAsync(new SystemEventRecord
        {
            Category = EventCategory.Health,
            EventType = SpeedtestNotificationTopics.VpnRestarted,
            Severity = EventSeverity.Warning,
            Message = "old restart",
            Timestamp = DateTime.UtcNow.AddMinutes(-90)
        });

        await job.Execute(CancellationToken.None);

        await Assert.That(control.RestartCount).IsEqualTo(1);

        Cleanup(factory);
    }

    static void Cleanup(TestDbContextFactory factory) => factory.Dispose();

    sealed class TestDbContextFactory : IDbContextFactory<ArrkDbContext>, IDisposable
    {
        readonly DbContextOptions<ArrkDbContext> _options;
        readonly string _dbPath;

        public TestDbContextFactory(DbContextOptions<ArrkDbContext> options, string dbPath)
        {
            _options = options;
            _dbPath = dbPath;
        }

        public ArrkDbContext CreateDbContext() => new(_options, []);
        public ValueTask<ArrkDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ArrkDbContext(_options, []));

        public void Dispose()
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        }
    }

    sealed class StubVpnSpeedtestClient(ArrkInstance instance, HttpClient client) : VpnSpeedtestClient(instance)
    {
        public override HttpClient CreateClient(TimeSpan? timeout = null) => client;
    }

    sealed class StubGluetunControlClient(ArrkInstance instance)
        : GluetunControlClient(instance, NullLogger<GluetunControlClient>.Instance)
    {
        public int RestartCount { get; private set; }

        public override Task RestartAsync(CancellationToken ct = default)
        {
            RestartCount++;
            return Task.CompletedTask;
        }
    }
}
