using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core.Configuration;

namespace Arrk.Core.Events;

/// <summary>
/// Trims the durable event log by age. The default retention applies to every event; a topic can
/// override it (for example <c>job.#</c> is kept for 7 days because background jobs produce a lot
/// of audit rows). A configured 0 means "keep forever".
/// </summary>
public sealed class EventPurgeJob(
    IEventStore events,
    IOptionsMonitor<ArrkConfig> config,
    TimeProvider time,
    ILogger<EventPurgeJob> logger)
{
    public const string DefaultCron = "30 4 * * *";

    [JobDisplayName("Event Cleanup")]
    public async Task Execute(CancellationToken ct)
    {
        EventRetentionConfig retention = config.CurrentValue.Events.Retention;
        if (!retention.Enabled)
            return;

        DateTime now = time.GetUtcNow().UtcDateTime;
        int defaultDays = Math.Max(0, retention.DefaultRetentionDays);
        DateTime defaultCutoff = defaultDays == 0
            ? DateTime.MinValue
            : now.AddDays(-defaultDays);

        Dictionary<string, DateTime?> topicCutoffs = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string topic, int days) in retention.TopicRetentionDays)
        {
            if (string.IsNullOrWhiteSpace(topic))
                continue;

            int topicDays = Math.Max(0, days);
            topicCutoffs[topic] = topicDays == 0 ? null : now.AddDays(-topicDays);
        }

        int removed = await events.PurgeAsync(defaultCutoff, topicCutoffs, ct);
        if (removed > 0)
            logger.LogInformation("Purged {Count} events outside their retention window", removed);
    }
}
