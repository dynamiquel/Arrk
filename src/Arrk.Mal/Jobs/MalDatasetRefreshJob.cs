using Hangfire;
using Microsoft.Extensions.Logging;
using Arrk.Core.Modules;
using Arrk.Mal.Services;

namespace Arrk.Mal.Jobs;

/// <summary>Refreshes the anime-lists mapping dataset when it is older than the configured age.</summary>
public sealed class MalDatasetRefreshJob(MalMappingDataset dataset, ILogger<MalDatasetRefreshJob> logger)
{
    public const string DefaultCron = "0 4 * * *";

    [JobDisplayName("MyAnimeList Mapping Refresh")]
    [SingleFlight]
    public async Task Execute(CancellationToken ct)
    {
        try
        {
            await dataset.EnsureLoadedAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not refresh the MyAnimeList mapping dataset");
        }
    }
}
