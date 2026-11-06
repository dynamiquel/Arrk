using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Enums;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Prowlarr;
using Arrk.Prowlarr.Configuration;
using Arrk.Tvarr;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Jobs;
using Arrk.Tvarr.Models;
using Arrk.Tvarr.Services;
using Arrk.Transmission;
using Arrk.TestSupport;

namespace Arrk.IntegrationTests;

/// <summary>
/// End-to-end cross-module tests where Arrk's jobs talk to REAL Sonarr, Radarr, Prowlarr and
/// Transmission containers. These validate the wiring that only makes sense when multiple real
/// applications are present (e.g. Prowlarr pointing at Radarr/Sonarr, both pointing at Transmission).
/// Skipped automatically when Docker is unavailable.
/// </summary>
[NotInParallel(ArrStackShared.ParallelKey)]
public class CrossModuleIntegrationTests
{
    static Task<ArrStackFixture?> GetFixtureAsync() => ArrStackShared.GetAsync();

    static ArrkInstance Instance(ArrStackFixture fixture)
    {
        // GetContainerName("radarr") must equal fixture.RadarrHost.
        return new ArrkInstance(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = fixture.StackName })
            .Build());
    }

    static ServiceRegistry Registry(params (IServiceDefinition Definition, bool Healthy)[] services)
    {
        ServiceRegistry registry = new();
        foreach ((IServiceDefinition definition, bool healthy) in services)
        {
            Service service = new(definition);
            service.State.AggregateState = healthy ? ContainerState.Running : ContainerState.Stopped;
            registry.Register(service);
        }
        return registry;
    }

    static RadarrClient Radarr(ArrStackFixture f) => new(f.RadarrUrl, f.RadarrApiKey);
    static SonarrClient Sonarr(ArrStackFixture f) => new(f.SonarrUrl, f.SonarrApiKey);
    static ProwlarrClient Prowlarr(ArrStackFixture f) => new(f.ProwlarrUrl, f.ProwlarrApiKey);

    static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    // Root folder sync: Sonarr + Radarr

    [Test]
    public async Task RootFolderSync_CreatesMoviesAndShowsOnRealArrs()
    {
        ArrStackFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        RadarrClient radarr = Radarr(fixture);
        SonarrClient sonarr = Sonarr(fixture);

        // Ensure the media directories exist and are writable inside the containers.
        string moviesPath = await CreateWritableDir(fixture.RadarrContainerId, "/media/it-movies");
        string showsPath = await CreateWritableDir(fixture.SonarrContainerId, "/media/it-shows");

        try
        {
            await radarr.AddRootFolder(moviesPath);
            await sonarr.AddRootFolder(showsPath);

            List<RootFolder> radarrFolders = await radarr.GetRootFolders();
            List<RootFolder> sonarrFolders = await sonarr.GetRootFolders();

            await Assert.That(radarrFolders.Any(f => f.Path == moviesPath)).IsTrue();
            await Assert.That(sonarrFolders.Any(f => f.Path == showsPath)).IsTrue();
        }
        finally
        {
            await CleanupRootFolders(radarr, sonarr, moviesPath, showsPath);
        }
    }

    // Download client sync: Transmission -> Radarr + Sonarr

    [Test]
    public async Task DownloadClientSync_WiresTransmissionIntoBothArrs()
    {
        ArrStackFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        RadarrClient radarr = Radarr(fixture);
        SonarrClient sonarr = Sonarr(fixture);
        DownloadClientSyncJob job = new(
            Registry((new TvServiceDefinition(), true), (new TransmissionServiceDefinition(), true)),
            radarr,
            sonarr,
            Instance(fixture),
            EmptyConfig(),
            NullLogger<DownloadClientSyncJob>.Instance);

        await RemoveTransmissionClients(radarr, sonarr);
        try
        {
            await job.Execute(CancellationToken.None);

            DownloadClientDto? radarrClient = (await radarr.GetDownloadClients())
                .FirstOrDefault(c => c.Implementation.Equals("Transmission", StringComparison.OrdinalIgnoreCase));
            DownloadClientDto? sonarrClient = (await sonarr.GetDownloadClients())
                .FirstOrDefault(c => c.Implementation.Equals("Transmission", StringComparison.OrdinalIgnoreCase));

            await Assert.That(radarrClient).IsNotNull();
            await Assert.That(sonarrClient).IsNotNull();

            string host = FieldValue(radarrClient!, "host");
            await Assert.That(host).IsEqualTo(fixture.TransmissionHost);
            await Assert.That(FieldValue(radarrClient!, "port")).IsEqualTo("9091");

            int radarrClientId = radarrClient!.Id;
            int sonarrClientId = sonarrClient!.Id;

            // Second run must be a no-op. Arr masks the password as "********"; if that were treated
            // as a mismatch the client would be deleted and recreated every run (new id).
            await job.Execute(CancellationToken.None);

            DownloadClientDto? radarrAfter = (await radarr.GetDownloadClients())
                .FirstOrDefault(c => c.Implementation.Equals("Transmission", StringComparison.OrdinalIgnoreCase));
            DownloadClientDto? sonarrAfter = (await sonarr.GetDownloadClients())
                .FirstOrDefault(c => c.Implementation.Equals("Transmission", StringComparison.OrdinalIgnoreCase));

            await Assert.That(radarrAfter).IsNotNull();
            await Assert.That(sonarrAfter).IsNotNull();
            await Assert.That(radarrAfter!.Id).IsEqualTo(radarrClientId);
            await Assert.That(sonarrAfter!.Id).IsEqualTo(sonarrClientId);
            await Assert.That((await radarr.GetDownloadClients())
                .Count(c => c.Implementation.Equals("Transmission", StringComparison.OrdinalIgnoreCase))).IsEqualTo(1);
        }
        finally
        {
            await RemoveTransmissionClients(radarr, sonarr);
        }
    }

    [Test]
    public async Task DownloadClientSync_BadCredentials_StillCreatesConfiguredClient()
    {
        ArrStackFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        RadarrClient radarr = Radarr(fixture);
        SonarrClient sonarr = Sonarr(fixture);
        // Point the instance at a wrong transmission host to force a recreate every run.
        DownloadClientSyncJob job = new(
            Registry((new TvServiceDefinition(), true), (new TransmissionServiceDefinition(), true)),
            radarr,
            sonarr,
            Instance(fixture),
            EmptyConfig(),
            NullLogger<DownloadClientSyncJob>.Instance);

        await RemoveTransmissionClients(radarr, sonarr);
        try
        {
            await job.Execute(CancellationToken.None);
            await job.Execute(CancellationToken.None);

            int count = (await radarr.GetDownloadClients())
                .Count(c => c.Implementation.Equals("Transmission", StringComparison.OrdinalIgnoreCase));
            await Assert.That(count).IsEqualTo(1);
        }
        finally
        {
            await RemoveTransmissionClients(radarr, sonarr);
        }
    }

    // Prowlarr app sync: Radarr + Sonarr registered in Prowlarr

    [Test]
    public async Task ProwlarrAppSync_RegistersRadarrAndSonarrInRealProwlarr()
    {
        ArrStackFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        ProwlarrClient prowlarr = Prowlarr(fixture);
        ProwlarrAppSyncJob job = new(
            Registry((new ProwlarrServiceDefinition(), true), (new TvServiceDefinition(), true)),
            prowlarr,
            new MutableOptionsMonitor<TvConfig>(new TvConfig()),
            new MutableOptionsMonitor<ProwlarrConfig>(new ProwlarrConfig()),
            Instance(fixture),
            NullLogger<ProwlarrAppSyncJob>.Instance);

        await RemoveApps(prowlarr);
        try
        {
            await job.Execute(CancellationToken.None);

            List<ProwlarrApplication> apps = await prowlarr.GetApplications();
            ProwlarrApplication? radarr = apps.FirstOrDefault(a => a.Name == "Radarr");
            ProwlarrApplication? sonarr = apps.FirstOrDefault(a => a.Name == "Sonarr");

            await Assert.That(radarr).IsNotNull();
            await Assert.That(sonarr).IsNotNull();
            await Assert.That(FieldValue(radarr!, "baseUrl")).IsEqualTo($"http://{fixture.RadarrHost}:7878/radarr");
            await Assert.That(FieldValue(sonarr!, "baseUrl")).IsEqualTo($"http://{fixture.SonarrHost}:8989/sonarr");
            await Assert.That(FieldValue(radarr!, "prowlarrUrl")).IsEqualTo($"http://{fixture.ProwlarrHost}:9696/prowlarr");

            // Idempotent on second run.
            await job.Execute(CancellationToken.None);
            await Assert.That((await prowlarr.GetApplications()).Count(a => a.Name is "Radarr" or "Sonarr")).IsEqualTo(2);
        }
        finally
        {
            await RemoveApps(prowlarr);
        }
    }

    [Test]
    public async Task ProwlarrAppSync_WhenProwlarrUnhealthy_DoesNothing()
    {
        ArrStackFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        ProwlarrClient prowlarr = Prowlarr(fixture);
        ProwlarrAppSyncJob job = new(
            Registry((new ProwlarrServiceDefinition(), false), (new TvServiceDefinition(), true)),
            prowlarr,
            new MutableOptionsMonitor<TvConfig>(new TvConfig()),
            new MutableOptionsMonitor<ProwlarrConfig>(new ProwlarrConfig()),
            Instance(fixture),
            NullLogger<ProwlarrAppSyncJob>.Instance);

        await RemoveApps(prowlarr);
        await job.Execute(CancellationToken.None);

        await Assert.That(await prowlarr.GetApplications()).IsEmpty();
    }

    // Quality profile sync: Radarr + Sonarr

    [Test]
    public async Task QualityProfileSync_CreatesCustomFormatsAndProfiles()
    {
        ArrStackFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        RadarrClient radarr = Radarr(fixture);
        SonarrClient sonarr = Sonarr(fixture);
        QualityProfilesConfig config = new()
        {
            Profiles =
            [
                new QualityProfileDefinition
                {
                    Name = "IntegrationProfile",
                    AllowedQualities = [QualitySource.Bluray, QualitySource.Web, QualitySource.Remux],
                    MinResolution = "1080p",
                    MaxResolution = "2160p"
                }
            ]
        };
        QualityProfileSyncJob job = new(
            Registry((new TvServiceDefinition(), true)),
            radarr,
            sonarr,
            new MutableOptionsMonitor<QualityProfilesConfig>(config),
            NullLogger<QualityProfileSyncJob>.Instance);

        await job.Execute(CancellationToken.None);

        try
        {
            List<QualityProfile> radarrProfiles = await radarr.GetQualityProfiles();
            List<QualityProfile> sonarrProfiles = await sonarr.GetQualityProfiles();
            await Assert.That(radarrProfiles.Any(p => p.Name == "IntegrationProfile")).IsTrue();
            await Assert.That(sonarrProfiles.Any(p => p.Name == "IntegrationProfile")).IsTrue();

            List<CustomFormatDto> radarrFormats = await radarr.GetCustomFormats();
            await Assert.That(radarrFormats.Any(f => f.Name == "Dolby Vision")).IsTrue();
            await Assert.That(radarrFormats.Any(f => f.Name == "DTS-HD MA")).IsTrue();
        }
        finally
        {
            await RemoveProfiles(radarr, sonarr, "IntegrationProfile");
        }
    }

    [Test]
    public async Task QualityDefinitionSync_AppliesUhdFloorToRealArrs()
    {
        ArrStackFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        RadarrClient radarr = Radarr(fixture);
        SonarrClient sonarr = Sonarr(fixture);
        QualityProfilesConfig config = new();

        List<QualityDefinitionDto> radarrBefore = await radarr.GetQualityDefinitions();
        List<QualityDefinitionDto> sonarrBefore = await sonarr.GetQualityDefinitions();

        int radarrChanged = await QualityDefinitionSync.ApplyAsync(
            radarr, config, "Radarr", NullLogger.Instance);
        int sonarrChanged = await QualityDefinitionSync.ApplyAsync(
            sonarr, config, "Sonarr", NullLogger.Instance);

        await Assert.That(radarrChanged).IsGreaterThan(0);
        await Assert.That(sonarrChanged).IsGreaterThan(0);

        await AssertUhdFloor(radarrBefore, await radarr.GetQualityDefinitions());
        await AssertUhdFloor(sonarrBefore, await sonarr.GetQualityDefinitions());

        // A second pass is a no-op: the job runs every 30 seconds.
        await Assert.That(await QualityDefinitionSync.ApplyAsync(
            radarr, config, "Radarr", NullLogger.Instance)).IsEqualTo(0);
        await Assert.That(await QualityDefinitionSync.ApplyAsync(
            sonarr, config, "Sonarr", NullLogger.Instance)).IsEqualTo(0);
    }

    static async Task AssertUhdFloor(
        List<QualityDefinitionDto> before,
        List<QualityDefinitionDto> after)
    {
        foreach (QualityDefinitionDto definition in after)
        {
            QualityDefinitionDto original = before.Single(d => d.Id == definition.Id);

            if (definition.Quality.Resolution == 2160)
                await Assert.That(definition.MinSize).IsEqualTo(90d);
            else
                await Assert.That(definition.MinSize).IsEqualTo(original.MinSize);
        }
    }

    [Test]
    public async Task QualityProfileSync_WhenTvUnhealthy_DoesNothing()
    {
        ArrStackFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        RadarrClient radarr = Radarr(fixture);
        QualityProfileSyncJob job = new(
            Registry((new TvServiceDefinition(), false)),
            radarr,
            Sonarr(fixture),
            new MutableOptionsMonitor<QualityProfilesConfig>(new QualityProfilesConfig { Profiles = [] }),
            NullLogger<QualityProfileSyncJob>.Instance);

        // Should not throw despite the arr being reachable.
        await job.Execute(CancellationToken.None);
    }

    // Torrent cleanup: deleted torrent -> arr queue removal

    [Test]
    public async Task TorrentCleanup_NoMatchingQueueItem_CompletesWithoutDeleting()
    {
        ArrStackFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        RadarrClient radarr = Radarr(fixture);
        SonarrClient sonarr = Sonarr(fixture);
        RadarrService radarrService = new(radarr, NullLogger<RadarrService>.Instance);
        SonarrService sonarrService = new(sonarr, NullLogger<SonarrService>.Instance);

        // No matching queue item exists for this hash; DeleteQueueItemByHash must be a safe no-op.
        await radarrService.DeleteQueueItemByHash("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef");
        await sonarrService.DeleteQueueItemByHash("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef");

        // Both arr queues remain queryable and unchanged (empty on a fresh stack).
        await Assert.That(await radarr.GetQueue()).IsEmpty();
        await Assert.That(await sonarr.GetQueue()).IsEmpty();
    }

    // Concurrency across modules

    [Test]
    public async Task Concurrent_GetMoviesAndSeries_BothSucceed()
    {
        ArrStackFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        RadarrService radarr = new(Radarr(fixture), NullLogger<RadarrService>.Instance);
        SonarrService sonarr = new(Sonarr(fixture), NullLogger<SonarrService>.Instance);

        Task<List<TvMovie>> m1 = radarr.GetAllMovies();
        Task<List<TvSeries>> s1 = sonarr.GetAllSeries();
        Task<List<TvMovie>> m2 = radarr.GetAllMovies();
        Task<List<TvSeries>> s2 = sonarr.GetAllSeries();

        await Task.WhenAll(m1, s1, m2, s2);

        await Assert.That(m1.Result).IsNotNull();
        await Assert.That(s1.Result).IsNotNull();
        await Assert.That(m2.Result).IsNotNull();
        await Assert.That(s2.Result).IsNotNull();
    }

    // Helpers

    static string FieldValue(DownloadClientDto client, string name)
        => client.Fields.FirstOrDefault(f => f.Name == name)?.Value?.ToString() ?? "";

    static string FieldValue(ProwlarrApplication app, string name)
        => app.Fields.FirstOrDefault(f => f.Name == name)?.Value?.ToString() ?? "";

    static async Task<string> CreateWritableDir(string containerId, string path)
    {
        await ServiceContainerHelper.ExecInContainer(containerId, $"mkdir -p {path} && chown abc:abc {path} && chmod 777 {path}");
        return path;
    }

    static async Task CleanupRootFolders(RadarrClient radarr, SonarrClient sonarr, string moviesPath, string showsPath)
    {
        try
        {
            foreach (RootFolder folder in await radarr.GetRootFolders())
            {
                if (folder.Path == moviesPath)
                    await radarr.DeleteRootFolder(folder.Id);
            }
            foreach (RootFolder folder in await sonarr.GetRootFolders())
            {
                if (folder.Path == showsPath)
                    await sonarr.DeleteRootFolder(folder.Id);
            }
        }
        catch { }
    }

    static async Task RemoveTransmissionClients(RadarrClient radarr, SonarrClient sonarr)
    {
        foreach (DownloadClientDto client in await radarr.GetDownloadClients())
        {
            if (client.Implementation.Equals("Transmission", StringComparison.OrdinalIgnoreCase))
                await radarr.DeleteDownloadClient(client.Id);
        }
        foreach (DownloadClientDto client in await sonarr.GetDownloadClients())
        {
            if (client.Implementation.Equals("Transmission", StringComparison.OrdinalIgnoreCase))
                await sonarr.DeleteDownloadClient(client.Id);
        }
    }

    static async Task RemoveApps(ProwlarrClient prowlarr)
    {
        foreach (ProwlarrApplication app in await prowlarr.GetApplications())
        {
            if (app.Name is "Radarr" or "Sonarr")
                await prowlarr.DeleteApplication(app.Id);
        }
    }

    static async Task RemoveProfiles(RadarrClient radarr, SonarrClient sonarr, string name)
    {
        foreach (QualityProfile profile in await radarr.GetQualityProfiles())
        {
            if (profile.Name == name) await radarr.DeleteQualityProfile(profile.Id);
        }
        foreach (QualityProfile profile in await sonarr.GetQualityProfiles())
        {
            if (profile.Name == name) await sonarr.DeleteQualityProfile(profile.Id);
        }
    }

}
