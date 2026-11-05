using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Services;

namespace Stackarr.Jellyfin;

/// <summary>
/// Waits for the Jellyfin container to become healthy, then runs the one-off configuration
/// (startup wizard, admin account, libraries, theme). Recurring drift is handled by
/// <see cref="Jobs.JellyfinConfigSyncJob"/>.
/// </summary>
public class JellyfinInitialiser(
    ILogger<JellyfinInitialiser> logger,
    ServiceRegistry serviceRegistry,
    JellyfinService jellyfin,
    JellyfinSsoInstaller sso) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Service? service = serviceRegistry.Get(JellyfinServiceDefinition.StaticKey);
                if (service?.State.IsHealthy == true)
                {
                    await jellyfin.ConfigureAsync(stoppingToken);
                    await sso.EnsureAsync(stoppingToken);
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Jellyfin is not ready for configuration yet; retrying");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
