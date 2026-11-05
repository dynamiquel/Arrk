using Hangfire;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Modules;
using Stackarr.Mal.Services;

namespace Stackarr.Mal.Jobs;

/// <summary>Pushes newly completed local plays to every linked MyAnimeList account.</summary>
public sealed class MalWatchSyncJob(MalSyncService sync, ILogger<MalWatchSyncJob> logger)
{
    public const string DefaultCron = "*/10 * * * *";

    [JobDisplayName("MyAnimeList Watch Sync")]
    [SingleFlight]
    public async Task Execute(CancellationToken ct)
    {
        try
        {
            await sync.SyncAllAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "MyAnimeList watch sync failed");
        }
    }
}
