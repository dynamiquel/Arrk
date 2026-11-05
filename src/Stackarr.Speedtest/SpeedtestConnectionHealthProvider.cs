using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.Speedtest.Jobs;

namespace Stackarr.Speedtest;

/// <summary>
/// Turns the most recent stored speed test into a connection-health verdict for the torrent
/// download path. When the downloader is VPN-routed the VPN result is used, otherwise the host
/// (network) result, so torrent cleanup is judged against the connection it actually uses.
/// </summary>
/// <remarks>
/// Only successful results count (a failed test has no speed). When no result is available, or the
/// health threshold is disabled, the connection is reported healthy: a missing measurement must
/// never make torrents immune to, or responsible for, cleanup.
/// </remarks>
public sealed class SpeedtestConnectionHealthProvider(
    IDbContextFactory<StackarrDbContext> dbContextFactory,
    IOptionsMonitor<SpeedtestConfig> configMonitor,
    IConfiguration configuration) : IConnectionHealthProvider
{
    public async Task<ConnectionHealth> GetDownloadConnectionHealthAsync(CancellationToken ct = default)
    {
        string target = configuration.IsTorrentVpnRouted() ? VpnSpeedtestJob.Target : SpeedtestJob.Target;

        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);

        SpeedtestResult? latest = await db.SpeedtestResults
            .Where(x => x.Target == target && x.ErrorMessage == null)
            .OrderByDescending(x => x.TestedAt)
            .FirstOrDefaultAsync(ct);

        if (latest is null)
            return ConnectionHealth.Unknown(target);

        SpeedtestConfig config = configMonitor.CurrentValue;
        bool healthy = config.MinHealthyDownloadMbps <= 0
            || latest.DownloadMbps >= config.MinHealthyDownloadMbps;

        return new ConnectionHealth
        {
            IsHealthy = healthy,
            IsKnown = true,
            DownloadMbps = latest.DownloadMbps,
            TestedAt = latest.TestedAt,
            Target = target
        };
    }
}
