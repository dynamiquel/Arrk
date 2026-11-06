using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arrk.Mal.Services;

/// <summary>
/// Loads the anime-lists mapping dataset in the background after startup so opening a series
/// dialog or the watchlist picker never waits for a 5 MB download. Failures are non-fatal; the
/// nightly refresh job retries, and features degrade to MAL-only metadata until it succeeds.
/// </summary>
public sealed class MalDatasetWarmup(
    MalMappingDataset dataset,
    IOptionsMonitor<MalConfig> config,
    ILogger<MalDatasetWarmup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.CurrentValue.Enabled)
            return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            await dataset.EnsureLoadedAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            /* shutdown */
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not warm the MyAnimeList mapping dataset");
        }
    }
}
