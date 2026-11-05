using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Services;
using Stackarr.Prowlarr;
using Stackarr.Prowlarr.Configuration;

namespace Stackarr.Tvarr.Jobs;

public class ProwlarrAppSyncJob(
    ServiceRegistry serviceRegistry,
    ProwlarrClient prowlarr,
    IOptionsMonitor<TvConfig> tvConfig,
    IOptionsMonitor<ProwlarrConfig> prowlarrConfig,
    StackarrInstance stackarr,
    ILogger<ProwlarrAppSyncJob> logger)
{
    public const string DefaultCron = "*/30 * * * * *";

    [JobDisplayName("Prowlarr Application Sync")]
    public async Task Execute(CancellationToken ct)
    {
        Service? indexerService = serviceRegistry.Get("indexer");
        Service? tvService = serviceRegistry.Get(TvServiceDefinition.StaticKey);

        if (indexerService?.State.IsHealthy != true)
        {
            logger.LogDebug("Prowlarr indexer service not healthy, skipping app sync");
            return;
        }

        if (tvService?.State.IsHealthy != true)
        {
            logger.LogDebug("TV service not healthy, skipping app sync");
            return;
        }

        List<ProwlarrApplication> existingApps = await prowlarr.GetApplications(ct);

        string radarrUrl = $"http://{stackarr.GetContainerName("radarr")}:7878/radarr";
        string sonarrUrl = $"http://{stackarr.GetContainerName("sonarr")}:8989/sonarr";
        string prowlarrUrl = $"http://{stackarr.GetContainerName("prowlarr")}:9696/prowlarr";

        await SyncApplication(existingApps, "Radarr", "RadarrSettings", radarrUrl, prowlarrUrl, "2000", ct);
        await SyncApplication(existingApps, "Sonarr", "SonarrSettings", sonarrUrl, prowlarrUrl, "5000", ct);
    }

    async Task SyncApplication(
        List<ProwlarrApplication> existingApps,
        string name,
        string configContract,
        string targetUrl,
        string prowlarrUrl,
        string syncCategories,
        CancellationToken ct)
    {
        ProwlarrApplication? existing = existingApps.FirstOrDefault(
            a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        ProwlarrApplication app = new()
        {
            Name = name,
            Implementation = name,
            ConfigContract = configContract,
            Enable = true,
            SyncLevel = "fullSync",
            Fields =
            [
                new ProwlarrField { Name = "prowlarrUrl", Value = prowlarrUrl, Required = true },
                new ProwlarrField { Name = "baseUrl", Value = targetUrl, Required = true },
                new ProwlarrField { Name = "apiKey", Value = TvModule.ServiceApiKey, Required = true },
                new ProwlarrField { Name = "syncCategories", Value = syncCategories }
            ]
        };

        try
        {
            if (existing != null)
            {
                app.Id = existing.Id;
                await prowlarr.UpdateApplication(app, ct);
                logger.LogDebug("Updated Prowlarr application for {Name}", name);
            }
            else
            {
                await prowlarr.CreateApplication(app, ct);
                logger.LogInformation("Created Prowlarr application for {Name}", name);
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Failed to sync Prowlarr application for {Name}: {Reason}", name, ex.Message);
        }
    }
}
