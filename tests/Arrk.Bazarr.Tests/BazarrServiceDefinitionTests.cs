using Microsoft.Extensions.Configuration;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;

namespace Arrk.Bazarr.Tests;

public class BazarrServiceDefinitionTests
{
    static ArrkInstance Instance() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = "abc" })
            .Build());

    static IConfiguration Configuration(bool enabled = true, bool proxy = false) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["services:subtitles:bazarr:enabled"] = enabled.ToString(),
                ["services:subtitles:bazarr:proxy"] = proxy.ToString()
            })
            .Build();

    [Test]
    public async Task IsEnabled_RespectsTheSubtitlesFlag()
    {
        BazarrServiceDefinition definition = new();

        await Assert.That(definition.IsEnabled(Configuration(enabled: true))).IsTrue();
        await Assert.That(definition.IsEnabled(Configuration(enabled: false))).IsFalse();
    }

    [Test]
    public async Task Container_MountsMediaReadWriteForSidecarSubtitles()
    {
        BazarrServiceDefinition definition = new();

        ContainerSpec spec = definition.BuildContainerSpecs(Configuration(), new VolumePathResolver(new StorageConfig()), Instance()).Single();

        VolumeMount movies = spec.Volumes.Single(mount => mount.ContainerPath == "/movies");
        VolumeMount shows = spec.Volumes.Single(mount => mount.ContainerPath == "/shows");

        await Assert.That(spec.Image).IsEqualTo("lscr.io/linuxserver/bazarr:latest");
        await Assert.That(movies.Permissions).IsEqualTo(VolumePerms.ReadWrite);
        await Assert.That(shows.Permissions).IsEqualTo(VolumePerms.ReadWrite);
        await Assert.That(spec.Labels[ContainerLabels.Service]).IsEqualTo("bazarr");
    }

    [Test]
    public async Task Proxy_IsOptIn()
    {
        BazarrServiceDefinition definition = new();

        ContainerSpec direct = definition.BuildContainerSpecs(
            Configuration(proxy: false), new VolumePathResolver(new StorageConfig()), Instance()).Single();
        ContainerSpec proxied = definition.BuildContainerSpecs(
            Configuration(proxy: true), new VolumePathResolver(new StorageConfig()), Instance()).Single();

        await Assert.That(direct.Traefik).IsNull();
        await Assert.That(proxied.Traefik).IsNotNull();
        await Assert.That(proxied.Traefik!.Port).IsEqualTo(6767);
        await Assert.That(proxied.Traefik.StripPrefix).IsEqualTo("/bazarr");
    }
}
