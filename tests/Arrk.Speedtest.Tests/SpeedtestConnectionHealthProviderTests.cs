using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Speedtest.Jobs;
using Arrk.TestSupport;

namespace Arrk.Speedtest.Tests;

public class SpeedtestConnectionHealthProviderTests
{
    static IConfiguration Config(bool torrentVpnRouted) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["vpn:enabled"] = torrentVpnRouted.ToString().ToLowerInvariant(),
                ["services:torrentDownloader:vpn"] = torrentVpnRouted.ToString().ToLowerInvariant()
            })
            .Build();

    static async Task<SpeedtestConnectionHealthProvider> ProviderAsync(
        SpeedtestConfig config,
        bool torrentVpnRouted,
        params SpeedtestResult[] results)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"stackarr-connhealth-{Guid.NewGuid():N}.db");
        DbContextOptions<ArrkDbContext> options = new DbContextOptionsBuilder<ArrkDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using (ArrkDbContext setup = new(options, []))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.SpeedtestResults.AddRange(results);
            await setup.SaveChangesAsync();
        }

        TestDbContextFactory factory = new(options, dbPath);
        return new SpeedtestConnectionHealthProvider(
            factory, new MutableOptionsMonitor<SpeedtestConfig>(config), Config(torrentVpnRouted));
    }

    static SpeedtestResult Result(
        string target,
        double downloadMbps,
        DateTime? testedAt = null,
        string? error = null) => new()
        {
            Target = target,
            DownloadMbps = downloadMbps,
            TestedAt = testedAt ?? DateTime.UtcNow,
            ErrorMessage = error
        };

    [Test]
    public async Task NoResult_IsReportedHealthyAndUnknown()
    {
        SpeedtestConnectionHealthProvider provider = await ProviderAsync(new SpeedtestConfig(), torrentVpnRouted: false);

        ConnectionHealth health = await provider.GetDownloadConnectionHealthAsync();

        await Assert.That(health.IsKnown).IsFalse();
        await Assert.That(health.IsHealthy).IsTrue();
        await Assert.That(health.Target).IsEqualTo(SpeedtestJob.Target);
    }

    [Test]
    public async Task NetworkResultAboveThreshold_IsHealthy()
    {
        SpeedtestConnectionHealthProvider provider = await ProviderAsync(
            new SpeedtestConfig(), torrentVpnRouted: false, Result("network", 100));

        ConnectionHealth health = await provider.GetDownloadConnectionHealthAsync();

        await Assert.That(health.IsKnown).IsTrue();
        await Assert.That(health.IsHealthy).IsTrue();
        await Assert.That(health.DownloadMbps).IsEqualTo(100);
    }

    [Test]
    public async Task NetworkResultBelowThreshold_IsUnhealthy()
    {
        SpeedtestConnectionHealthProvider provider = await ProviderAsync(
            new SpeedtestConfig { MinHealthyDownloadMbps = 8 }, torrentVpnRouted: false, Result("network", 2));

        ConnectionHealth health = await provider.GetDownloadConnectionHealthAsync();

        await Assert.That(health.IsHealthy).IsFalse();
    }

    [Test]
    public async Task ThresholdDisabled_IsAlwaysHealthy()
    {
        SpeedtestConnectionHealthProvider provider = await ProviderAsync(
            new SpeedtestConfig { MinHealthyDownloadMbps = 0 }, torrentVpnRouted: false, Result("network", 0.1));

        ConnectionHealth health = await provider.GetDownloadConnectionHealthAsync();

        await Assert.That(health.IsHealthy).IsTrue();
    }

    [Test]
    public async Task VpnRouted_UsesVpnResultNotNetwork()
    {
        SpeedtestConnectionHealthProvider provider = await ProviderAsync(
            new SpeedtestConfig { MinHealthyDownloadMbps = 8 },
            torrentVpnRouted: true,
            Result("network", 1),
            Result("vpn", 100));

        ConnectionHealth health = await provider.GetDownloadConnectionHealthAsync();

        await Assert.That(health.Target).IsEqualTo(VpnSpeedtestJob.Target);
        await Assert.That(health.IsHealthy).IsTrue();
        await Assert.That(health.DownloadMbps).IsEqualTo(100);
    }

    [Test]
    public async Task OnlyFailedResult_IsReportedUnknown()
    {
        SpeedtestConnectionHealthProvider provider = await ProviderAsync(
            new SpeedtestConfig(), torrentVpnRouted: false, Result("network", 0, error: "boom"));

        ConnectionHealth health = await provider.GetDownloadConnectionHealthAsync();

        await Assert.That(health.IsKnown).IsFalse();
        await Assert.That(health.IsHealthy).IsTrue();
    }

    [Test]
    public async Task UsesLatestSuccessfulResult()
    {
        DateTime now = DateTime.UtcNow;
        SpeedtestConnectionHealthProvider provider = await ProviderAsync(
            new SpeedtestConfig { MinHealthyDownloadMbps = 8 },
            torrentVpnRouted: false,
            Result("network", 100, now.AddMinutes(-30)),
            Result("network", 2, now));

        ConnectionHealth health = await provider.GetDownloadConnectionHealthAsync();

        await Assert.That(health.DownloadMbps).IsEqualTo(2);
        await Assert.That(health.IsHealthy).IsFalse();
    }

    sealed class TestDbContextFactory(DbContextOptions<ArrkDbContext> options, string dbPath)
        : IDbContextFactory<ArrkDbContext>, IDisposable
    {
        public ArrkDbContext CreateDbContext() => new(options, []);

        public ValueTask<ArrkDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ArrkDbContext(options, []));

        public void Dispose()
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

}
