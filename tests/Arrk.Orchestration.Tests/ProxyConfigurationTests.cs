using Microsoft.Extensions.Configuration;
using Arrk.Bazarr;
using Arrk.Bazarr;
using Arrk.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Jellyfin;
using Arrk.Music;
using Arrk.Navidrome;
using Arrk.Prowlarr;
using Arrk.Transmission;
using Arrk.Tvarr;

namespace Arrk.Orchestration.Tests;

/// <summary>
/// Covers the per-service reverse-proxy flag introduced so services are only exposed through
/// Traefik when explicitly enabled (media players on by default, everything else off).
/// </summary>
public class ProxyConfigurationTests
{
    static (ArrkConfig Config, VolumePathResolver Resolver, ArrkInstance Arrk) Context()
    {
        ArrkConfig config = ConfigDefaults.GetDefaults();
        return (config, new VolumePathResolver(config.Storage), OrchestrationTestHelper.CreateArrkInstance());
    }

    static IConfiguration Empty() => TestHelper.EmptyConfiguration();

    [Test]
    public async Task IsServiceProxied_UsesDefault_WhenFlagAbsent()
    {
        IConfiguration configuration = Empty();

        await Assert.That(configuration.IsServiceProxied("services:jellyfin", defaultValue: true)).IsTrue();
        await Assert.That(configuration.IsServiceProxied("services:tv", defaultValue: false)).IsFalse();
    }

    [Test]
    public async Task IsServiceProxied_RespectsExplicitValue()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["services:tv:proxy"] = "true",
                ["services:jellyfin:proxy"] = "false"
            })
            .Build();

        await Assert.That(configuration.IsServiceProxied("services:tv", defaultValue: false)).IsTrue();
        await Assert.That(configuration.IsServiceProxied("services:jellyfin", defaultValue: true)).IsFalse();
    }

    [Test]
    public async Task MediaPlayers_AreProxiedByDefault()
    {
        (ArrkConfig config, VolumePathResolver resolver, ArrkInstance arrk) = Context();
        IConfiguration configuration = Empty();

        await Assert.That(new JellyfinServiceDefinition().BuildContainerSpecs(configuration, resolver, arrk).Single().Traefik).IsNotNull();
        await Assert.That(new NavidromeServiceDefinition().BuildContainerSpecs(configuration, resolver, arrk).Single().Traefik).IsNotNull();
    }

    [Test]
    public async Task OtherServices_AreNotProxiedByDefault()
    {
        (ArrkConfig config, VolumePathResolver resolver, ArrkInstance arrk) = Context();
        IConfiguration configuration = Empty();

        foreach (ContainerSpec spec in new TvServiceDefinition().BuildContainerSpecs(configuration, resolver, arrk))
            await Assert.That(spec.Traefik).IsNull();
        await Assert.That(new Arrk.Slskd.SlskdServiceDefinition().BuildContainerSpecs(configuration, resolver, arrk).Single().Traefik).IsNull();
        await Assert.That(new ProwlarrServiceDefinition().BuildContainerSpecs(configuration, resolver, arrk).Single().Traefik).IsNull();
        await Assert.That(new TransmissionServiceDefinition().BuildContainerSpecs(configuration, resolver, arrk).Single().Traefik).IsNull();
        await Assert.That(new BazarrServiceDefinition().BuildContainerSpecs(configuration, resolver, arrk).Single().Traefik).IsNull();
    }

    [Test]
    public async Task ExplicitProxyFalse_RemovesTraefik_ForMediaPlayers()
    {
        (ArrkConfig config, VolumePathResolver resolver, ArrkInstance arrk) = Context();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["services:jellyfin:proxy"] = "false",
                ["services:navidrome:proxy"] = "false"
            })
            .Build();

        await Assert.That(new JellyfinServiceDefinition().BuildContainerSpecs(configuration, resolver, arrk).Single().Traefik).IsNull();
        await Assert.That(new NavidromeServiceDefinition().BuildContainerSpecs(configuration, resolver, arrk).Single().Traefik).IsNull();
    }

    [Test]
    public async Task ExplicitProxyTrue_AddsTraefik_ForArrServices()
    {
        (ArrkConfig config, VolumePathResolver resolver, ArrkInstance arrk) = Context();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["services:tv:proxy"] = "true",
                ["services:slskd:proxy"] = "true",
                ["indexers:proxy"] = "true",
                ["services:torrentDownloader:proxy"] = "true",
                ["services:subtitles:bazarr:proxy"] = "true"
            })
            .Build();

        foreach (ContainerSpec spec in new TvServiceDefinition().BuildContainerSpecs(configuration, resolver, arrk))
            await Assert.That(spec.Traefik).IsNotNull();
        await Assert.That(new Arrk.Slskd.SlskdServiceDefinition().BuildContainerSpecs(configuration, resolver, arrk).Single().Traefik).IsNotNull();
        await Assert.That(new ProwlarrServiceDefinition().BuildContainerSpecs(configuration, resolver, arrk).Single().Traefik).IsNotNull();
        await Assert.That(new TransmissionServiceDefinition().BuildContainerSpecs(configuration, resolver, arrk).Single().Traefik).IsNotNull();
        await Assert.That(new BazarrServiceDefinition().BuildContainerSpecs(configuration, resolver, arrk).Single().Traefik).IsNotNull();
    }

    [Test]
    public async Task Navidrome_SetsBaseUrl_OnlyWhenProxied()
    {
        (ArrkConfig config, VolumePathResolver resolver, ArrkInstance arrk) = Context();

        ContainerSpec proxied = new NavidromeServiceDefinition()
            .BuildContainerSpecs(Empty(), resolver, arrk).Single();
        await Assert.That(proxied.Environment["ND_BASEURL"]).IsEqualTo("/navidrome");

        IConfiguration disabled = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["services:navidrome:proxy"] = "false" })
            .Build();
        ContainerSpec unproxied = new NavidromeServiceDefinition()
            .BuildContainerSpecs(disabled, resolver, arrk).Single();
        await Assert.That(unproxied.Environment.ContainsKey("ND_BASEURL")).IsFalse();
    }

    [Test]
    public async Task Navidrome_KeepsItsDataFolderOnThePersistentVolume()
    {
        (ArrkConfig config, VolumePathResolver resolver, ArrkInstance arrk) = Context();

        ContainerSpec spec = new NavidromeServiceDefinition().BuildContainerSpecs(Empty(), resolver, arrk).Single();

        // Navidrome keeps its database, config, cache and plugins under DataFolder, so the persistent
        // volume must mount at /data. Navidrome never reads /config, and an anonymous volume there
        // would lose playlists and installed plugins when the container is recreated.
        await Assert.That(spec.Environment["ND_DATAFOLDER"]).IsEqualTo("/data");
        await Assert.That(spec.Volumes.Any(v => v.ContainerPath == "/data" && v.Type == VolumeType.NamedVolume)).IsTrue();
        await Assert.That(spec.Volumes.Any(v => v.ContainerPath == "/config")).IsFalse();
    }

    [Test]
    public async Task ArrServices_SetUrlBase_RegardlessOfProxy()
    {
        (ArrkConfig config, VolumePathResolver resolver, ArrkInstance arrk) = Context();

        // Radarr/Sonarr/Prowlarr API clients depend on the URL base, so it stays set even
        // when the service is not exposed through the proxy.
        List<ContainerSpec> specs = [.. new TvServiceDefinition().BuildContainerSpecs(Empty(), resolver, arrk)];
        await Assert.That(specs[0].Environment["RADARR__SERVER__URLBASE"]).IsEqualTo("/radarr");
        await Assert.That(specs[1].Environment["SONARR__SERVER__URLBASE"]).IsEqualTo("/sonarr");
        await Assert.That(new ProwlarrServiceDefinition().BuildContainerSpecs(Empty(), resolver, arrk).Single().Environment["PROWLARR__SERVER__URLBASE"]).IsEqualTo("/prowlarr");
    }

    [Test]
    public async Task Bazarr_UsesStripPrefixMiddleware()
    {
        (ArrkConfig config, VolumePathResolver resolver, ArrkInstance arrk) = Context();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["services:subtitles:bazarr:proxy"] = "true" })
            .Build();

        TraefikConfig? traefik = new BazarrServiceDefinition()
            .BuildContainerSpecs(configuration, resolver, arrk).Single().Traefik;

        await Assert.That(traefik).IsNotNull();
        await Assert.That(traefik!.StripPrefix).IsEqualTo("/bazarr");
    }

}
