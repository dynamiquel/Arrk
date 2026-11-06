using Hangfire;
using Microsoft.Extensions.Logging;
using Arrk.Orchestration;

namespace Arrk.Slskd;

/// <summary>
/// Restarts the slskd container after it has been disconnected from Soulseek for a couple of minutes,
/// which resets slskd's growing reconnect backoff. Restarting goes through the orchestration module's
/// service restart.
/// </summary>
public class SlskdReconnectJob(
    SlskdService slskd,
    SlskdConnectionWatchdog watchdog,
    ArrkOrchestrator orchestrator,
    ILogger<SlskdReconnectJob> logger)
{
    public const string DefaultCron = "* * * * *";

    [JobDisplayName("Soulseek Reconnect Watchdog")]
    public async Task Execute(CancellationToken ct)
    {
        if (!slskd.IsEnabled)
            return;

        bool available = await slskd.IsAvailableAsync(ct);
        DateTime now = DateTime.UtcNow;
        if (!watchdog.ShouldRestart(available, now))
            return;

        logger.LogWarning(
            "Soulseek has been disconnected for over {Minutes:0} min; restarting the slskd container to reset its reconnect backoff",
            SlskdConnectionWatchdog.DisconnectThreshold.TotalMinutes);

        bool restarted = await orchestrator.RestartServiceAsync(SlskdServiceDefinition.StaticKey, ct);
        watchdog.RecordRestart(now);

        if (!restarted)
            logger.LogWarning("Could not restart the slskd container; will retry after the cooldown");
    }
}
