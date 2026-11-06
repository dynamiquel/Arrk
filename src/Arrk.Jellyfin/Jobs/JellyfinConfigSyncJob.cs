using Hangfire;
using Microsoft.Extensions.Logging;
using Arrk.Core.Services;

namespace Arrk.Jellyfin.Jobs;

/// <summary>
/// Periodically re-applies the Arrk Jellyfin configuration so drift (manual changes, a
/// rebuilt container, a config edit) is corrected. The underlying <see cref="JellyfinService"/>
/// is idempotent, so an already-correct server produces no writes.
/// </summary>
public class JellyfinConfigSyncJob(
    ServiceRegistry serviceRegistry,
    JellyfinService jellyfin,
    JellyfinSsoInstaller sso,
    ILogger<JellyfinConfigSyncJob> logger)
{
    public const string DefaultCron = "*/5 * * * *";

    [JobDisplayName("Jellyfin Config Sync")]
    public async Task Execute(CancellationToken ct)
    {
        Service? service = serviceRegistry.Get(JellyfinServiceDefinition.StaticKey);
        if (service?.State.IsHealthy != true)
        {
            logger.LogDebug("Jellyfin service not healthy, skipping config sync");
            return;
        }

        try
        {
            await jellyfin.ConfigureAsync(ct);
            await sso.EnsureAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Jellyfin config sync failed");
        }
    }
}
