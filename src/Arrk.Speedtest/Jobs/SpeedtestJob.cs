using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Notifications;
using Arrk.Speedtest.Models;

namespace Arrk.Speedtest.Jobs;

public class SpeedtestJob(
    CloudflareSpeedtestClient client,
    IDbContextFactory<ArrkDbContext> dbContextFactory,
    IOptionsMonitor<SpeedtestConfig> configMonitor,
    IEventStore eventStore,
    ILogger<SpeedtestJob> logger)
{
    public const string DefaultCron = "*/30 * * * *";
    public const string Target = "network";

    [JobDisplayName("Network Speed Test")]
    public async Task Execute(CancellationToken ct)
    {
        SpeedtestConfig config = configMonitor.CurrentValue;
        if (!config.Enabled)
        {
            return;
        }

        logger.LogInformation("Starting network speed test");

        CloudflareSpeedtestResult result = await client.RunTestAsync(ct);

        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);

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
            logger.LogWarning("Speed test failed: {Error}", result.ErrorMessage);
            return;
        }

        logger.LogInformation(
            "Speed test complete: {Download:F1} Mbps down / {Upload:F1} Mbps up / {Ping:F0} ms ping",
            result.DownloadMbps, result.UploadMbps, result.PingMs);

        await NotifyAsync(result, config, ct);
    }

    async Task NotifyAsync(CloudflareSpeedtestResult result, SpeedtestConfig config, CancellationToken ct)
    {
        bool below = IsBelowThreshold(result, config);
        bool wasDegraded = await IsCurrentlyDegradedAsync(ct);

        string summary =
            $"{result.DownloadMbps:F1} Mbps down / {result.UploadMbps:F1} Mbps up / {result.PingMs:F0} ms ping";

        IReadOnlyList<NotificationField> fields =
        [
            new("Download", $"{result.DownloadMbps:F1} Mbps"),
            new("Upload", $"{result.UploadMbps:F1} Mbps"),
            new("Ping", $"{result.PingMs:F0} ms")
        ];

        // Informational result for every successful test (off by default to avoid noise).
        await RecordAsync(
            SpeedtestNotificationTopics.SpeedtestCompleted, "Speed test complete", summary,
            EventSeverity.Info, fields, result.TestedAt, ct);

        if (below && !wasDegraded)
        {
            string message = $"Speed test below threshold: {DescribeProblems(result, config)}";
            await RecordAsync(
                SpeedtestNotificationTopics.SpeedtestDegraded, "Speed test degraded", message,
                EventSeverity.Warning, fields, result.TestedAt, ct);
        }
        else if (!below && wasDegraded)
        {
            await RecordAsync(
                SpeedtestNotificationTopics.SpeedtestRecovered, "Speed test recovered",
                $"Speed test back above threshold: {summary}",
                EventSeverity.Success, fields, result.TestedAt, ct);
        }
    }

    static bool IsBelowThreshold(CloudflareSpeedtestResult result, SpeedtestConfig config) =>
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

    /// <summary>
    /// Whether the most recent speed test state was degraded. Derived from the event log so it
    /// survives restarts and job re-activation.
    /// </summary>
    async Task<bool> IsCurrentlyDegradedAsync(CancellationToken ct)
    {
        IReadOnlyList<SystemEvent> recent = await eventStore.QueryAsync(new SystemEventQuery
        {
            Category = EventCategory.Health,
            Search = "speedtest",
            Limit = 100
        }, ct);

        SystemEvent? latest = recent.FirstOrDefault(e =>
            e.EventType is "speedtest.degraded" or "speedtest.recovered");

        return latest?.EventType == "speedtest.degraded";
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
            SourceName = "Speed test",
            Notification = new NotificationMessage
            {
                Key = topic,
                Title = title,
                Body = message,
                Severity = severity,
                Source = "speedtest",
                SourceName = "Speed test",
                Timestamp = timestamp,
                Fields = fields
            },
            Timestamp = timestamp
        }, ct);
    }
}
