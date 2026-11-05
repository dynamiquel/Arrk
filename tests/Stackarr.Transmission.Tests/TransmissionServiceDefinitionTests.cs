using Microsoft.Extensions.Configuration;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.Transmission.Tests;

public class TransmissionServiceDefinitionTests
{
    static StackarrInstance CreateStackarr(string stackName = "test")
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = stackName })
            .Build();
        return new StackarrInstance(config);
    }

    static IConfiguration VpnConfig(bool enabled, bool proxy = false) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["services:torrentDownloader:vpn"] = enabled.ToString(),
                ["services:torrentDownloader:proxy"] = proxy.ToString()
            })
            .Build();

    static VolumePathResolver Resolver() => new(new StorageConfig());

    [Test]
    public async Task BuildContainerSpecs_UsesExpectedImageNameAndEnvironment()
    {
        TransmissionServiceDefinition definition = new();
        StackarrInstance stackarr = CreateStackarr("abc");

        ContainerSpec spec = definition.BuildContainerSpecs(VpnConfig(false), Resolver(), stackarr).Single();

        await Assert.That(spec.Image).IsEqualTo("lscr.io/linuxserver/transmission:latest");
        await Assert.That(spec.Name).IsEqualTo("stackarr-abc-transmission");
        await Assert.That(spec.Environment["USER"]).IsEqualTo("transmission");
        await Assert.That(spec.Environment["PASS"]).IsEqualTo("stackarr");
        await Assert.That(spec.Environment["RPC_USERNAME"]).IsEqualTo("transmission");
        await Assert.That(spec.Environment["RPC_PASSWORD"]).IsEqualTo("stackarr");
        await Assert.That(spec.Environment["RPC_PORT"]).IsEqualTo("9091");
        await Assert.That(spec.Environment["PEERPORT"]).IsEqualTo("51413");
        await Assert.That(spec.Labels[ContainerLabels.Service]).IsEqualTo("torrent_downloader");
        await Assert.That(spec.NetworkName).IsEqualTo("stackarr-abc-net");
    }

    [Test]
    public async Task BuildContainerSpecs_MountsConfigVolumeAndDownloads()
    {
        TransmissionServiceDefinition definition = new();
        StackarrInstance stackarr = CreateStackarr("abc");

        ContainerSpec spec = definition.BuildContainerSpecs(VpnConfig(false), Resolver(), stackarr).Single();

        VolumeMount configVolume = spec.Volumes.Single(v => v.ContainerPath == "/config");
        await Assert.That(configVolume.HostPath).IsEqualTo("stackarr-abc-transmission-config");
        await Assert.That(configVolume.Type).IsEqualTo(VolumeType.NamedVolume);
        await Assert.That(configVolume.Permissions).IsEqualTo(VolumePerms.ReadWrite);

        VolumeMount downloads = spec.Volumes.Single(v => v.ContainerPath == "/downloads");
        await Assert.That(downloads.HostPath).IsEqualTo(RelativeVolumes.Downloads);
        await Assert.That(downloads.Type).IsEqualTo(VolumeType.Bind);
    }

    [Test]
    public async Task BuildContainerSpecs_VpnDisabled_ExposesTraefikAndNoNetworkMode()
    {
        TransmissionServiceDefinition definition = new();
        StackarrInstance stackarr = CreateStackarr();

        ContainerSpec spec = definition.BuildContainerSpecs(VpnConfig(false, proxy: true), Resolver(), stackarr).Single();

        await Assert.That(spec.NetworkMode).IsNull();
        await Assert.That(spec.Traefik).IsNotNull();
        await Assert.That(spec.Traefik!.ServiceName).IsEqualTo("transmission");
        await Assert.That(spec.Traefik.Port).IsEqualTo(9091);
    }

    [Test]
    public async Task BuildContainerSpecs_VpnEnabled_UsesVpnNetworkModeAndKeepsTraefikMetadata()
    {
        TransmissionServiceDefinition definition = new();
        StackarrInstance stackarr = CreateStackarr("abc");

        ContainerSpec spec = definition.BuildContainerSpecs(VpnConfig(true, proxy: true), Resolver(), stackarr).Single();

        await Assert.That(spec.NetworkMode).IsEqualTo("container:stackarr-abc-gluetun");
        // Traefik metadata is retained so the provisioner can emit a file-provider route
        // pointing at the Gluetun container; it is not turned into Docker labels.
        await Assert.That(spec.Traefik).IsNotNull();
        await Assert.That(spec.Traefik!.ServiceName).IsEqualTo("transmission");
        await Assert.That(spec.Traefik.Port).IsEqualTo(9091);
    }


}
