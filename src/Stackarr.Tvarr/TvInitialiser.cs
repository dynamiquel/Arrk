using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core.Services;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Models;

namespace Stackarr.Tvarr;

public class TvInitialiser(
    ILogger<TvInitialiser> logger,
    IOptionsMonitor<TvConfig> tvConfig,
    ServiceRegistry serviceRegistry,
    RadarrClient radarr,
    SonarrClient sonarr) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            try
            {
                Service? tvService = serviceRegistry.Get(TvServiceDefinition.StaticKey);
                if (tvService?.State.IsHealthy == true)
                {
                    await CreateRootFolders(stoppingToken);
                    await CreateQualityProfiles(stoppingToken);
                    break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An exception occurred while initialising the service");
            }
            finally
            {
                logger.LogInformation("Could not initialise TV yet. Will try again.");
            }
        }
    }

    async Task CreateRootFolders(CancellationToken cancellationToken)
    {
        logger.LogInformation("Checking for expected root media folders for Sonarr and Radarr...");

        List<RootFolder> sonarrRootFolders = await sonarr.GetRootFolders(cancellationToken);
        List<RootFolder> radarrRootFolders = await radarr.GetRootFolders(cancellationToken);

        if (sonarrRootFolders.All(f => f.Path != "/shows"))
        {
            logger.LogInformation("Did not find an expected /shows root media folder for Sonarr. Creating now...");
            await sonarr.AddRootFolder("/shows", cancellationToken);
            logger.LogInformation("Bound /shows root media folder for Sonarr");
        }

        if (radarrRootFolders.All(f => f.Path != "/movies"))
        {
            logger.LogInformation("Did not find an expected /movies root media folder for Radarr. Creating now...");
            await radarr.AddRootFolder("/movies", cancellationToken);
            logger.LogInformation("Bound /movies root media folder for Radarr");
        }
    }

    async Task CreateQualityProfiles(CancellationToken cancellationToken)
    {
        List<QualityProfile> sonarrQualityProfiles = await sonarr.GetQualityProfiles(cancellationToken);
        List<QualityProfile> radarrQualityProfiles = await radarr.GetQualityProfiles(cancellationToken);
    }
}
