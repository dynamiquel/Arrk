using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core.Services;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Jobs;

/// <summary>
/// Retries monitored content that is still missing once an hour.
///
/// <para>
/// Deleting a failed torrent triggers an immediate re-search, but that search can find nothing (no
/// seeders, all releases rejected) and there is no second attempt until another failure event. This
/// sweep gives every missing movie/episode a fresh chance without waiting for a deletion, and it
/// also backfills content that predates the failed-download handling.
/// </para>
/// </summary>
public class MissingContentSearchJob(
    ServiceRegistry serviceRegistry,
    SonarrService sonarr,
    RadarrService radarr,
    IOptionsMonitor<TvConfig> config,
    ILogger<MissingContentSearchJob> logger)
{
    public const string DefaultCron = "0 * * * *";

    [JobDisplayName("Missing Content Search")]
    public async Task Execute(CancellationToken ct)
    {
        if (!config.CurrentValue.MissingSearch.Enabled)
            return;

        Service? tvService = serviceRegistry.Get(TvServiceDefinition.StaticKey);
        if (tvService?.State.IsHealthy != true)
            return;

        await SafeSearchAsync(() => sonarr.SearchMissingAsync(ct), "Sonarr", ct);
        await SafeSearchAsync(() => radarr.SearchMissingAsync(ct), "Radarr", ct);
        logger.LogInformation("Triggered missing-content search in Sonarr and Radarr");
    }

    async Task SafeSearchAsync(Func<Task> search, string service, CancellationToken ct)
    {
        try
        {
            await search();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // One unreachable arr must not stop the other from being swept.
            logger.LogWarning(ex, "Missing content search failed for {Service}", service);
        }
    }
}
