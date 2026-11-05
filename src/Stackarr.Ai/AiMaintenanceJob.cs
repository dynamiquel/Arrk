using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stackarr.Ai;

/// <summary>
/// Daily housekeeping for AI data: trims usage history to the configured retention and drops
/// expired cache entries. Both are bounded and never touch live state.
/// </summary>
public sealed class AiMaintenanceJob(
    AiUsageService usage,
    AiCache cache,
    IOptionsMonitor<AiConfig> config,
    TimeProvider time,
    ILogger<AiMaintenanceJob> logger)
{
    public const string DefaultCron = "0 3 * * *";

    [JobDisplayName("AI Cleanup")]
    public async Task Execute(CancellationToken ct)
    {
        AiConfig current = config.CurrentValue;

        int cacheRemoved = await cache.PurgeExpiredAsync(ct);
        if (cacheRemoved > 0)
            logger.LogInformation("Purged {Count} expired AI cache entries", cacheRemoved);

        if (current.Limits.UsageRetentionDays <= 0)
            return;

        DateTime cutoff = time.GetUtcNow().UtcDateTime.AddDays(-current.Limits.UsageRetentionDays);
        int usageRemoved = await usage.PurgeBeforeAsync(cutoff, ct);
        if (usageRemoved > 0)
            logger.LogInformation("Purged {Count} AI usage rows older than {Days} days",
                usageRemoved, current.Limits.UsageRetentionDays);
    }
}
