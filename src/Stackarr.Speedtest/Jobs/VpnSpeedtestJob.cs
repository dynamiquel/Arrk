using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Events;
using Stackarr.Core.Models;
using Stackarr.Core.Notifications;
using Stackarr.Core.Services;
using Stackarr.Speedtest.Models;

namespace Stackarr.Speedtest.Jobs;

/// <summary>
/// Runs the same Cloudflare speed test as <see cref="SpeedtestJob"/> but through the VPN
/// (Gluetun) container, storing the result under the <c>vpn</c> target so it can be shown on a
/// dedicated tile. Only runs when the VPN tunnel is required and the VPN speed test is enabled.
/// </summary>
public class VpnSpeedtestJob(
    CloudflareSpeedtestClient client,
    VpnSpeedtestClient vpnClient,
    IDbContextFactory<StackarrDbContext> dbContextFactory,
    IOptionsMonitor<SpeedtestConfig> configMonitor,
    IOptionsMonitor<VpnConfig> vpnConfigMonitor,
    GluetunControlClient gluetunControl,
    IConfiguration configuration,
    IEventStore eventStore,
    ILogger<VpnSpeedtestJob> logger)
{
    public const string DefaultCron = "*/30 * * * *";
    public const string Target = "vpn";

    [JobDisplayName("VPN Speed Test")]
    public async Task Execute(CancellationToken ct)
    {
        SpeedtestConfig config = configMonitor.CurrentValue;
        if (!config.Enabled || !configuration.IsVpnTunnelRequired())
        {
            return;
        }

        logger.LogInformation("Starting VPN speed test through {Proxy}", vpnClient.ProxyAddress);

        using HttpClient httpClient = vpnClient.CreateClient();
        CloudflareSpeedtestResult result = await client.RunTestAsync(httpClient, ct);

        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);

        bool wasDegraded = await WasPreviouslyDegradedAsync(db, config, ct);

        db.SpeedtestResults.Add(new SpeedtestResult
        {
            DownloadMbps = result.DownloadMbps,
            UploadMbps = result.UploadMbps,
            PingMs = result.PingMs,
            JitterMs = result.JitterMs,
            Target = Target,
            TestedAt = result.TestedAt,
            ErrorMessage = result.ErrorMessage
        });

        await db.SaveChangesAsync(ct);

        if (result.IsError)
        {
            logger.LogWarning("VPN speed test failed: {Error}", result.ErrorMessage);
            return;
        }

        logger.LogInformation(
            "VPN speed test complete: {Download:F1} Mbps down / {Upload:F1} Mbps up / {Ping:F0} ms ping",
            result.DownloadMbps, result.UploadMbps, result.PingMs);

        await NotifyAsync(result, config, wasDegraded, ct);

        // On a degraded test that is also below the very-low restart thresholds, bounce the
        // tunnel in place to force a fresh provider reconnection (cooldown-guarded).
        VpnConfig vpnConfig = vpnConfigMonitor.CurrentValue;
        if (IsBelowThreshold(result, config) && ShouldRestart(vpnConfig, result))
            await RestartTunnelAsync(vpnConfig, result, ct);
    }

    static async Task<bool> WasPreviouslyDegradedAsync(StackarrDbContext db, SpeedtestConfig config, CancellationToken ct)
    {
        SpeedtestResult? previous = await db.SpeedtestResults
            .Where(x => x.Target == Target && x.ErrorMessage == null)
            .OrderByDescending(x => x.TestedAt)
            .FirstOrDefaultAsync(ct);

        return previous != null && IsBelowThreshold(previous, config);
    }

    async Task NotifyAsync(CloudflareSpeedtestResult result, SpeedtestConfig config, bool wasDegraded, CancellationToken ct)
    {
        bool below = IsBelowThreshold(result, config);

        string summary =
            $"{result.DownloadMbps:F1} Mbps down / {result.UploadMbps:F1} Mbps up / {result.PingMs:F0} ms ping";

        IReadOnlyList<NotificationField> fields =
        [
            new("Download", $"{result.DownloadMbps:F1} Mbps"),
            new("Upload", $"{result.UploadMbps:F1} Mbps"),
            new("Ping", $"{result.PingMs:F0} ms")
        ];

        await RecordAsync(
            SpeedtestNotificationTopics.VpnSpeedtestCompleted, "VPN speed test complete", summary,
            EventSeverity.Info, fields, result.TestedAt, ct);

        if (below && !wasDegraded)
        {
            string message = $"VPN speed test below threshold: {DescribeProblems(result, config)}";
            await RecordAsync(
                SpeedtestNotificationTopics.VpnSpeedtestDegraded, "VPN speed test degraded", message,
                EventSeverity.Warning, fields, result.TestedAt, ct);
        }
        else if (!below && wasDegraded)
        {
            await RecordAsync(
                SpeedtestNotificationTopics.VpnSpeedtestRecovered, "VPN speed test recovered",
                $"VPN speed test back above threshold: {summary}",
                EventSeverity.Success, fields, result.TestedAt, ct);
        }
    }

    static bool IsBelowThreshold(CloudflareSpeedtestResult result, SpeedtestConfig config) =>
        (config.MinDownloadMbps > 0 && result.DownloadMbps < config.MinDownloadMbps)
        || (config.MinUploadMbps > 0 && result.UploadMbps < config.MinUploadMbps);

    static bool IsBelowThreshold(SpeedtestResult result, SpeedtestConfig config) =>
        (config.MinDownloadMbps > 0 && result.DownloadMbps < config.MinDownloadMbps)
        || (config.MinUploadMbps > 0 && result.UploadMbps < config.MinUploadMbps);

    static string DescribeProblems(CloudflareSpeedtestResult result, SpeedtestConfig config)
    {
        List<string> problems = [];
        if (config.MinDownloadMbps > 0 && result.DownloadMbps < config.MinDownloadMbps)
            problems.Add($"download {result.DownloadMbps:F1} < {config.MinDownloadMbps} Mbps");
        if (config.MinUploadMbps > 0 && result.UploadMbps < config.MinUploadMbps)
            problems.Add($"upload {result.UploadMbps:F1} < {config.MinUploadMbps} Mbps");
        return string.Join(", ", problems);
    }

    static bool ShouldRestart(VpnConfig vpn, CloudflareSpeedtestResult result) =>
        vpn.Restart.Enabled
        && ((vpn.Restart.DownloadThreshold > 0 && result.DownloadMbps < vpn.Restart.DownloadThreshold)
            || (vpn.Restart.UploadThreshold > 0 && result.UploadMbps < vpn.Restart.UploadThreshold));

    /// <summary>
    /// Bounces the Gluetun tunnel (stopped -&gt; running) to force a fresh provider
    /// reconnection, unless the cooldown has not elapsed since the last restart.
    /// </summary>
    async Task RestartTunnelAsync(VpnConfig vpn, CloudflareSpeedtestResult result, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;

        if (await WasRestartedRecentlyAsync(vpn, now, ct))
        {
            logger.LogInformation(
                "VPN restart skipped: within {Minutes} min cooldown", vpn.Restart.CooldownMinutes);
            return;
        }

        try
        {
            await gluetunControl.RestartAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to restart VPN tunnel via the Gluetun control server");
            return;
        }

        logger.LogWarning("VPN tunnel restarted after a very low speed test: {Trigger}",
            DescribeRestartTrigger(result, vpn));

        await RecordAsync(
            SpeedtestNotificationTopics.VpnRestarted, "VPN tunnel restarted",
            $"VPN tunnel restarted: {DescribeRestartTrigger(result, vpn)}",
            EventSeverity.Warning,
            [
                new("Download", $"{result.DownloadMbps:F1} Mbps"),
                new("Upload", $"{result.UploadMbps:F1} Mbps"),
                new("Ping", $"{result.PingMs:F0} ms")
            ],
            now, ct);
    }

    async Task<bool> WasRestartedRecentlyAsync(VpnConfig vpn, DateTime now, CancellationToken ct)
    {
        if (vpn.Restart.CooldownMinutes <= 0)
            return false;

        IReadOnlyList<SystemEvent> recent = await eventStore.QueryAsync(new SystemEventQuery
        {
            Category = EventCategory.Health,
            EventType = SpeedtestNotificationTopics.VpnRestarted,
            Limit = 1
        }, ct);

        SystemEvent? latest = recent.FirstOrDefault();
        return latest is not null && latest.Timestamp > now.AddMinutes(-vpn.Restart.CooldownMinutes);
    }

    static string DescribeRestartTrigger(CloudflareSpeedtestResult result, VpnConfig vpn)
    {
        List<string> problems = [];
        if (vpn.Restart.DownloadThreshold > 0 && result.DownloadMbps < vpn.Restart.DownloadThreshold)
            problems.Add($"download {result.DownloadMbps:F1} < {vpn.Restart.DownloadThreshold} Mbps");
        if (vpn.Restart.UploadThreshold > 0 && result.UploadMbps < vpn.Restart.UploadThreshold)
            problems.Add($"upload {result.UploadMbps:F1} < {vpn.Restart.UploadThreshold} Mbps");
        return string.Join(", ", problems);
    }

    async Task RecordAsync(
        string topic,
        string title,
        string message,
        EventSeverity severity,
        IReadOnlyList<NotificationField> fields,
        DateTime timestamp,
        CancellationToken ct)
    {
        await eventStore.RecordAsync(new SystemEventRecord
        {
            Category = EventCategory.Health,
            EventType = topic,
            Severity = severity,
            Message = message,
            Source = "speedtest",
            SourceName = "VPN speed test",
            Notification = new NotificationMessage
            {
                Key = topic,
                Title = title,
                Body = message,
                Severity = severity,
                Source = "speedtest",
                SourceName = "VPN speed test",
                Timestamp = timestamp,
                Fields = fields
            },
            Timestamp = timestamp
        }, ct);
    }
}
