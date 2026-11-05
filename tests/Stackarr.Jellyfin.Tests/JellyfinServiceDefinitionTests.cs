using Microsoft.Extensions.Configuration;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.Jellyfin.Tests;

public class JellyfinServiceDefinitionTests
{
    static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    static StackarrInstance Instance() => new(Config(("StackName", "test")));

    static VolumePathResolver Resolver() => new(new StorageConfig());

    static ContainerSpec Spec(IConfiguration? configuration = null) =>
        new JellyfinServiceDefinition().BuildContainerSpecs(
            configuration ?? Config(), Resolver(), Instance())[0];

    [Test]
    public async Task IsEnabled_DefaultsToTrue_AndRespectsToggle()
    {
        await Assert.That(new JellyfinServiceDefinition().IsEnabled(Config())).IsTrue();
        await Assert.That(new JellyfinServiceDefinition().IsEnabled(Config(("services:jellyfin:enabled", "false")))).IsFalse();
    }

    [Test]
    public async Task Spec_HasExpectedImageNameAndNetwork()
    {
        ContainerSpec spec = Spec();

        await Assert.That(spec.Image).IsEqualTo("lscr.io/linuxserver/jellyfin:latest");
        await Assert.That(spec.Name).IsEqualTo("stackarr-test-jellyfin");
        await Assert.That(spec.Labels[ContainerLabels.Service]).IsEqualTo("jellyfin");
        await Assert.That(spec.NetworkName).IsEqualTo("stackarr-test-net");
    }

    [Test]
    public async Task Spec_MountsMediaFoldersAndConfig()
    {
        ContainerSpec spec = Spec();

        await Assert.That(spec.Volumes.Any(v => v.ContainerPath == "/config")).IsTrue();
        await Assert.That(spec.Volumes.Any(v => v.ContainerPath == "/movies")).IsTrue();
        await Assert.That(spec.Volumes.Any(v => v.ContainerPath == "/shows")).IsTrue();
        await Assert.That(spec.Volumes.Any(v => v.ContainerPath == "/music")).IsTrue();
    }

    [Test]
    public async Task Spec_DoesNotPublishHostPorts()
    {
        ContainerSpec spec = Spec();

        await Assert.That(spec.Ports).IsEmpty();
    }

    [Test]
    public async Task Spec_IsProxiedByDefault()
    {
        ContainerSpec spec = Spec();

        await Assert.That(spec.Traefik).IsNotNull();
        await Assert.That(spec.Traefik!.Rule).IsEqualTo("PathPrefix(`/jellyfin`)");
        await Assert.That(spec.Traefik.Port).IsEqualTo(JellyfinModule.HttpPort);
        await Assert.That(spec.Traefik.ServiceName).IsEqualTo("jellyfin");
    }

    [Test]
    public async Task Spec_WhenProxyDisabled_HasNoTraefik()
    {
        ContainerSpec spec = Spec(Config(("services:jellyfin:proxy", "false")));

        await Assert.That(spec.Traefik).IsNull();
    }

    [Test]
    public async Task Spec_WhenNvidiaEnabled_AddsRuntimeAndCapabilities()
    {
        ContainerSpec spec = Spec(Config(("services:jellyfin:nvidia", "true")));

        await Assert.That(spec.Runtime).IsEqualTo("nvidia");
        await Assert.That(spec.Capabilities).Contains("SYS_ADMIN");
        await Assert.That(spec.Environment["NVIDIA_VISIBLE_DEVICES"]).IsEqualTo("all");
        await Assert.That(spec.Environment["NVIDIA_DRIVER_CAPABILITIES"]).Contains("video");
    }
}
