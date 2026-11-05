using Microsoft.Extensions.Configuration;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.Tvarr.Tests;

public class TvServiceDefinitionTests
{
    static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    // TV services default to not proxied, so tests exercise the proxied spec by opting in.
    static IConfiguration ProxiedConfig() => Config(("services:tv:proxy", "true"));

    static StackarrInstance Instance() => new(Config(("StackName", "test")));

    static VolumePathResolver Resolver() => new(new StorageConfig());

    static IReadOnlyList<ContainerSpec> Specs(IConfiguration? configuration = null) =>
        new TvServiceDefinition().BuildContainerSpecs(
            configuration ?? ProxiedConfig(), Resolver(), Instance());

    [Test]
    public async Task ProvisionOrdering_ComesAfterInfrastructure()
    {
        TvServiceDefinition def = new();

        await Assert.That(def.ProvisionAfter).IsEquivalentTo(["indexer", "torrent_downloader", "bazarr"]);
        await Assert.That(def.ProvisionBefore).IsEmpty();
    }

    [Test]
    public async Task IsEnabled_DefaultsToTrue_AndHonoursFlag()
    {
        TvServiceDefinition def = new();

        await Assert.That(def.IsEnabled(Config())).IsTrue();
        await Assert.That(def.IsEnabled(Config(("services:tv:enabled", "false")))).IsFalse();
    }

    [Test]
    public async Task BuildContainerSpecs_ReturnsRadarrThenSonarr()
    {
        IReadOnlyList<ContainerSpec> specs = Specs();

        await Assert.That(specs.Count).IsEqualTo(2);
        await Assert.That(specs[0].Name).IsEqualTo("stackarr-test-radarr");
        await Assert.That(specs[1].Name).IsEqualTo("stackarr-test-sonarr");
    }

    [Test]
    public async Task Radarr_HasExpectedImageEnvAndTraefik()
    {
        ContainerSpec radarr = Specs()[0];

        await Assert.That(radarr.Image).IsEqualTo("lscr.io/linuxserver/radarr:latest");
        await Assert.That(radarr.Environment["RADARR__AUTH__APIKEY"]).IsEqualTo(TvModule.ServiceApiKey);
        await Assert.That(radarr.Environment["RADARR__SERVER__URLBASE"]).IsEqualTo("/radarr");
        await Assert.That(radarr.Labels[ContainerLabels.Service]).IsEqualTo("tv");
        await Assert.That(radarr.Traefik).IsNotNull();
        await Assert.That(radarr.Traefik!.Rule).IsEqualTo("PathPrefix(`/radarr`)");
        await Assert.That(radarr.Traefik.Port).IsEqualTo(7878);
        await Assert.That(radarr.NetworkName).IsEqualTo("stackarr-test-net");
    }

    [Test]
    public async Task Sonarr_HasExpectedImageEnvAndTraefik()
    {
        ContainerSpec sonarr = Specs()[1];

        await Assert.That(sonarr.Image).IsEqualTo("lscr.io/linuxserver/sonarr:latest");
        await Assert.That(sonarr.Environment["SONARR__AUTH__APIKEY"]).IsEqualTo(TvModule.ServiceApiKey);
        await Assert.That(sonarr.Environment["SONARR__SERVER__URLBASE"]).IsEqualTo("/sonarr");
        await Assert.That(sonarr.Traefik!.Rule).IsEqualTo("PathPrefix(`/sonarr`)");
        await Assert.That(sonarr.Traefik.Port).IsEqualTo(8989);
    }

    [Test]
    public async Task Radarr_MountsConfigMoviesAndDownloads()
    {
        ContainerSpec radarr = Specs()[0];

        await Assert.That(radarr.Volumes.Any(v => v.ContainerPath == "/config")).IsTrue();
        await Assert.That(radarr.Volumes.Any(v => v.ContainerPath == "/movies")).IsTrue();
        await Assert.That(radarr.Volumes.Any(v => v.ContainerPath == "/downloads")).IsTrue();
        VolumeMount config = radarr.Volumes.Single(v => v.ContainerPath == "/config");
        await Assert.That(config.Type).IsEqualTo(VolumeType.NamedVolume);
        await Assert.That(config.Permissions).IsEqualTo(VolumePerms.ReadWrite);
    }

    [Test]
    public async Task Sonarr_MountsConfigShowsAndDownloads()
    {
        ContainerSpec sonarr = Specs()[1];

        await Assert.That(sonarr.Volumes.Any(v => v.ContainerPath == "/shows")).IsTrue();
        await Assert.That(sonarr.Volumes.Any(v => v.ContainerPath == "/downloads")).IsTrue();
    }
}
