using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Services;

namespace Arrk.IntegrationTests;

/// <summary>
/// Verifies against real Radarr and Sonarr that Arrk can flip the "Redownload Failed"
/// download-handling setting through the arr API. Skipped automatically without Docker.
/// </summary>
[NotInParallel(ArrStackShared.ParallelKey)]
public class FailedDownloadHandlingIntegrationTests
{
    static RadarrClient Radarr(ArrStackFixture f) => new(f.RadarrUrl, f.RadarrApiKey);
    static SonarrClient Sonarr(ArrStackFixture f) => new(f.SonarrUrl, f.SonarrApiKey);

    [Test]
    public async Task Sync_EnablesAutoRedownloadFailedOnBothRealArrs()
    {
        ArrStackFixture? fixture = await ArrStackShared.GetAsync();
        if (fixture is null) return;

        RadarrClient radarr = Radarr(fixture);
        SonarrClient sonarr = Sonarr(fixture);

        // Start from the opposite state so the sync has to change a real setting.
        await ForceAsync(radarr, enabled: false);
        await ForceAsync(sonarr, enabled: false);

        FailedDownloadHandlingSync sync = new(
            radarr, sonarr, NullLogger<FailedDownloadHandlingSync>.Instance);

        FailedDownloadSyncResult first = await sync.SyncAsync(enabled: true);

        await Assert.That(first.RadarrChanged).IsTrue();
        await Assert.That(first.SonarrChanged).IsTrue();
        await Assert.That((await radarr.GetDownloadClientConfig())["autoRedownloadFailed"]!.GetValue<bool>()).IsTrue();
        await Assert.That((await sonarr.GetDownloadClientConfig())["autoRedownloadFailed"]!.GetValue<bool>()).IsTrue();

        // A second run sees the desired state and writes nothing.
        FailedDownloadSyncResult second = await sync.SyncAsync(enabled: true);
        await Assert.That(second.Changed).IsFalse();

        // The switch also disables it again.
        FailedDownloadSyncResult off = await sync.SyncAsync(enabled: false);
        await Assert.That(off.Changed).IsTrue();
        await Assert.That((await radarr.GetDownloadClientConfig())["autoRedownloadFailed"]!.GetValue<bool>()).IsFalse();
        await Assert.That((await sonarr.GetDownloadClientConfig())["autoRedownloadFailed"]!.GetValue<bool>()).IsFalse();

        // Leave the shared fixture in the production default.
        await sync.SyncAsync(enabled: true);
    }

    static async Task ForceAsync(ArrClient client, bool enabled)
    {
        System.Text.Json.Nodes.JsonObject config = await client.GetDownloadClientConfig();
        config["autoRedownloadFailed"] = enabled;
        await client.UpdateDownloadClientConfig(config);
    }
}
