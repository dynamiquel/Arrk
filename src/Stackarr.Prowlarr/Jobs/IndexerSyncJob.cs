using Hangfire;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Services;

namespace Stackarr.Prowlarr.Jobs;

public class IndexerSyncJob(
    ServiceRegistry serviceRegistry,
    ProwlarrService prowlarr,
    ILogger<IndexerSyncJob> logger)
{
    public const string DefaultCron = "*/30 * * * * *";

    [JobDisplayName("Prowlarr Indexer Sync")]
    public async Task Execute(CancellationToken ct)
    {
        Service? indexerService = serviceRegistry.Get("indexer");
        if (indexerService?.State.IsHealthy != true)
        {
            logger.LogDebug("Prowlarr indexer service not healthy, skipping indexer sync");
            return;
        }

        try
        {
            await prowlarr.SyncIndexers(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Indexer sync failed");
        }
    }
}
