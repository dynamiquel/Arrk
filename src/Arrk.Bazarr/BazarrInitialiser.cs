using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core.Services;

namespace Arrk.Bazarr;

/// <summary>
/// Waits for Bazarr and the TV backend to become healthy, then applies Arrk's subtitle defaults.
/// Recurring drift is handled by <see cref="Jobs.BazarrConfigSyncJob"/>.
/// </summary>
public sealed class BazarrInitialiser(
    ILogger<BazarrInitialiser> logger,
    IOptionsMonitor<BazarrConfig> config,
    IOptionsMonitor<SubtitlesConfig> subtitlesConfig,
    ServiceRegistry services,
    BazarrSubtitleProvider provider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!config.CurrentValue.Enabled)
                return;

            try
            {
                Service? bazarr = services.Get(BazarrServiceDefinition.StaticKey);
                Service? tv = services.Get("tv");
                if (bazarr?.State.IsHealthy == true && tv?.State.IsHealthy == true)
                {
                    await provider.ConfigureAsync(subtitlesConfig.CurrentValue.ToPreferences(), stoppingToken);
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bazarr is not ready for configuration yet; retrying");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
