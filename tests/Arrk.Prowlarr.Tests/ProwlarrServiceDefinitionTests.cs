using Microsoft.Extensions.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;

namespace Arrk.Prowlarr.Tests;

public class ProwlarrServiceDefinitionTests
{
    static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    // Prowlarr defaults to not proxied, so tests exercise the proxied spec by opting in.
    static IConfiguration ProxiedConfig() => Config(("indexers:proxy", "true"));

    static ArrkInstance Instance() => new(Config(("StackName", "test")));

    static VolumePathResolver Resolver() => new(new StorageConfig());

    static ContainerSpec Spec(IConfiguration? configuration = null) =>
        new ProwlarrServiceDefinition().BuildContainerSpecs(
            configuration ?? ProxiedConfig(), Resolver(), Instance())[0];

    [Test]
    public async Task ProvisionOrdering_IsBeforeContentServices()
    {
        ProwlarrServiceDefinition def = new();

        await Assert.That(def.ProvisionBefore).IsEquivalentTo(["tv", "music"]);
        await Assert.That(def.ProvisionAfter).IsEmpty();
    }

    [Test]
    public async Task Spec_HasExpectedImageNameEnvAndTraefik()
    {
        ContainerSpec spec = Spec();

        await Assert.That(spec.Image).IsEqualTo("lscr.io/linuxserver/prowlarr:latest");
        await Assert.That(spec.Name).IsEqualTo("stackarr-test-prowlarr");
        await Assert.That(spec.Environment["PROWLARR__AUTH__APIKEY"]).IsEqualTo(ProwlarrModule.ServiceApiKey);
        await Assert.That(spec.Environment["PROWLARR__SERVER__URLBASE"]).IsEqualTo("/prowlarr");
        await Assert.That(spec.Labels[ContainerLabels.Service]).IsEqualTo("indexer");
        await Assert.That(spec.Traefik).IsNotNull();
        await Assert.That(spec.Traefik!.Rule).IsEqualTo("PathPrefix(`/prowlarr`)");
        await Assert.That(spec.Traefik.Port).IsEqualTo(9696);
    }

    [Test]
    public async Task Spec_MountsConfigNamedVolume()
    {
        ContainerSpec spec = Spec();

        VolumeMount config = spec.Volumes.Single(v => v.ContainerPath == "/config");
        await Assert.That(config.Type).IsEqualTo(VolumeType.NamedVolume);
        await Assert.That(config.HostPath).IsEqualTo("stackarr-test-prowlarr-config");
        await Assert.That(config.Permissions).IsEqualTo(VolumePerms.ReadWrite);
    }

    [Test]
    public async Task Spec_WithoutVpn_UsesNormalNetworkMode()
    {
        ContainerSpec spec = Spec(Config(("vpn:enabled", "false"), ("indexers:proxy", "true")));

        await Assert.That(spec.NetworkMode).IsNull();
        await Assert.That(spec.Traefik).IsNotNull();
    }

    [Test]
    public async Task Spec_WithVpnEnabled_StillUsesNormalNetwork()
    {
        // Prowlarr never routes through the VPN sidecar; only the torrent downloader opts in.
        ContainerSpec spec = Spec(Config(("vpn:enabled", "true"), ("indexers:proxy", "true")));

        await Assert.That(spec.NetworkMode).IsNull();
        await Assert.That(spec.Traefik).IsNotNull();
    }

    // Cloudflare solver (Byparr) is a second container of the indexer service

    [Test]
    public async Task Spec_WhenSolverDisabled_ProducesOnlyProwlarr()
    {
        IReadOnlyList<ContainerSpec> specs = new ProwlarrServiceDefinition()
            .BuildContainerSpecs(Config(), Resolver(), Instance());

        await Assert.That(specs).HasCount(1);
        await Assert.That(specs[0].Name).IsEqualTo("stackarr-test-prowlarr");
    }

    [Test]
    public async Task Spec_WhenSolverEnabled_AddsByparrContainer_BelongingToIndexerService()
    {
        IConfiguration configuration = Config(("indexers:solver:enabled", "true"));

        IReadOnlyList<ContainerSpec> specs = new ProwlarrServiceDefinition()
            .BuildContainerSpecs(configuration, Resolver(), Instance());

        await Assert.That(specs).HasCount(2);

        ContainerSpec solver = specs.Single(s => s.Name == "stackarr-test-byparr");
        await Assert.That(solver.Image).IsEqualTo("ghcr.io/thephaseless/byparr:latest");
        await Assert.That(solver.Labels[ContainerLabels.Service]).IsEqualTo("indexer");
        await Assert.That(solver.NetworkName).IsEqualTo("stackarr-test-net");
        await Assert.That(solver.NetworkMode).IsNull();
        await Assert.That(solver.Ports).IsEmpty();
        await Assert.That(solver.Volumes).IsEmpty();
        await Assert.That(solver.Traefik).IsNull();
        await Assert.That(solver.Environment["TZ"]).IsEqualTo("Etc/UTC");
        await Assert.That(solver.RestartPolicy).IsEqualTo(RestartPolicyType.UnlessStopped);
    }

    [Test]
    public async Task Spec_WhenSolverEnabled_HonoursConfiguredImage()
    {
        IConfiguration configuration = Config(
            ("indexers:solver:enabled", "true"),
            ("indexers:solver:image", "ghcr.io/thephaseless/byparr:2.1.0"));

        IReadOnlyList<ContainerSpec> specs = new ProwlarrServiceDefinition()
            .BuildContainerSpecs(configuration, Resolver(), Instance());

        await Assert.That(specs.Single(s => s.Name == "stackarr-test-byparr").Image)
            .IsEqualTo("ghcr.io/thephaseless/byparr:2.1.0");
    }
}
