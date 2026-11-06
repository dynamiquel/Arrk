using Hangfire;
using Microsoft.Extensions.Logging;
using Arrk.Core.Services;

namespace Arrk.Jellyfin.Jobs;

/// <summary>
/// Asks Jellyfin to scan its libraries. Runs on a short schedule as a safety net for filesystem
/// watcher gaps, and is also triggered immediately when an arr reports an import.
/// </summary>
public class JellyfinLibraryScanJob(
    ServiceRegistry serviceRegistry,
    JellyfinClient client,
    ILogger<JellyfinLibraryScanJob> logger)
{
    public const string DefaultCron = "*/5 * * * *";

    [JobDisplayName("Jellyfin Library Scan")]
    public async Task Execute(CancellationToken ct)
    {
        Service? service = serviceRegistry.Get(JellyfinServiceDefinition.StaticKey);
        if (service?.State.IsHealthy != true)
        {
            logger.LogDebug("Jellyfin service not healthy, skipping library scan");
            return;
        }

        try
        {
            await client.TriggerLibraryRefreshAsync(ct);
            logger.LogDebug("Triggered Jellyfin library scan");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not trigger a Jellyfin library scan");
        }
    }
}
