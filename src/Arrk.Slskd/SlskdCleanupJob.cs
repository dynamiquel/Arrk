using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arrk.Slskd;

/// <summary>
/// Periodically removes failed Soulseek transfers so the unified Downloads page doesn't fill up with
/// dead entries. slskd keeps failed transfers until they are explicitly removed, and the ones we care
/// about (a source that errored, a rejected/timed-out peer) will never recover on their own.
/// </summary>
public class SlskdCleanupJob(
    SlskdService slskd,
    IOptionsMonitor<SlskdConfig> config,
    ILogger<SlskdCleanupJob> logger)
{
    public const string DefaultCron = "*/30 * * * *";

    [JobDisplayName("Soulseek Cleanup")]
    public async Task Execute(CancellationToken ct)
    {
        if (!slskd.IsEnabled)
            return;

        int hours = Math.Max(1, config.CurrentValue.FailedTransferRetentionHours);
        try
        {
            int removed = await slskd.RemoveFailedDownloadsAsync(TimeSpan.FromHours(hours), ct);
            if (removed > 0)
                logger.LogInformation("Removed {Count} failed Soulseek transfer(s) older than {Hours}h", removed, hours);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to clean up failed Soulseek transfers");
        }
    }
}
