using Hangfire;
using Microsoft.Extensions.Options;
using Arrk.Core.Services;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Jobs;

/// <summary>
/// Forces the arrs' own failed-download handling to match
/// <c>services.tv.failedDownloads.retriggerSearch</c>, so a release the arr rejects is blocklisted
/// and replaced even when Arrk never sees the torrent as errored.
/// </summary>
public class FailedDownloadHandlingSyncJob(
    ServiceRegistry serviceRegistry,
    FailedDownloadHandlingSync sync,
    IOptionsMonitor<TvConfig> config)
{
    public const string DefaultCron = "*/30 * * * * *";

    [JobDisplayName("Failed Download Handling Sync")]
    public async Task Execute(CancellationToken ct)
    {
        Service? tvService = serviceRegistry.Get(TvServiceDefinition.StaticKey);
        if (tvService?.State.IsHealthy != true)
            return;

        bool enabled = config.CurrentValue.FailedDownloads.RetriggerSearch;
        await sync.SyncAsync(enabled, ct);
    }
}
