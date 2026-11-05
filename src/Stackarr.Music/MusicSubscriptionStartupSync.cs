using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stackarr.Music.Services;

namespace Stackarr.Music;

/// <summary>
/// Re-syncs playlist subscriptions once shortly after startup, so a deploy/restart refreshes them
/// (matching, missing grabs and projected playlists) without waiting for the six-hourly job.
/// </summary>
public class MusicSubscriptionStartupSync(
    MusicSubscriptionService subscriptions,
    ILogger<MusicSubscriptionStartupSync> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), CancellationToken.None);
                int count = await subscriptions.SyncAllAsync();
                if (count > 0)
                    logger.LogInformation("Startup re-sync refreshed {Count} music subscription(s)", count);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Startup music subscription sync failed");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
