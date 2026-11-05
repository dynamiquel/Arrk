using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core.Configuration;

namespace Stackarr.Core.Notifications;

/// <summary>
/// Periodically trims the in-app notification inbox according to
/// <see cref="NotificationConfig.RetentionDays"/>. Delivery logs are not kept forever.
/// </summary>
public class NotificationPurgeJob(
    INotificationService notifications,
    IOptionsMonitor<NotificationConfig> config,
    TimeProvider time,
    ILogger<NotificationPurgeJob> logger)
{
    public const string DefaultCron = "0 4 * * *";

    [JobDisplayName("Notification Cleanup")]
    public async Task Execute(CancellationToken ct)
    {
        int retentionDays = config.CurrentValue.RetentionDays;
        if (retentionDays <= 0)
            return;

        DateTime cutoff = time.GetUtcNow().UtcDateTime.AddDays(-retentionDays);
        int removed = await notifications.PurgeOlderThanAsync(cutoff, ct);

        if (removed > 0)
            logger.LogInformation("Purged {Count} notifications older than {Days} days", removed, retentionDays);
    }
}
