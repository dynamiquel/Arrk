using Hangfire;
using Stackarr.Core.Services;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Models;

namespace Stackarr.Tvarr.Jobs;

public class RootFolderSyncJob(
    ServiceRegistry serviceRegistry,
    RadarrClient radarr,
    SonarrClient sonarr)
{
    public const string DefaultCron = "*/30 * * * * *";

    [JobDisplayName("Root Folder Sync")]
    public async Task Execute(CancellationToken ct)
    {
        Service? tvService = serviceRegistry.Get(
            TvServiceDefinition.StaticKey);
        if (tvService?.State.IsHealthy != true)
        {
            return;
        }

        List<RootFolder> sonarrRootFolders =
            await sonarr.GetRootFolders(ct);
        List<RootFolder> radarrRootFolders =
            await radarr.GetRootFolders(ct);

        if (sonarrRootFolders.All(f => f.Path != "/shows"))
        {
            await sonarr.AddRootFolder("/shows", ct);
        }

        if (radarrRootFolders.All(f => f.Path != "/movies"))
        {
            await radarr.AddRootFolder("/movies", ct);
        }
    }
}
