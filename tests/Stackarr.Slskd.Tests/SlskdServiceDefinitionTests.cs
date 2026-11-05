using Microsoft.Extensions.Configuration;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.Slskd.Tests;

/// <summary>Verifies the slskd container spec, especially that it honours the torrent limits.</summary>
public class SlskdServiceDefinitionTests
{
    static StackarrInstance CreateStackarr(string stackName = "test")
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = stackName })
            .Build();
        return new StackarrInstance(config);
    }

    static IConfiguration LimitsConfig(int maxDownloads = 5, int maxSeeds = 10, int upMbps = 10, int downMbps = 0, int? dlSlots = null, int? ulSlots = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["services:torrentDownloader:limits:maxActiveDownloads"] = maxDownloads.ToString(),
                ["services:torrentDownloader:limits:maxActiveSeeds"] = maxSeeds.ToString(),
                ["services:torrentDownloader:speed:uploadMbps"] = upMbps.ToString(),
                ["services:torrentDownloader:speed:downloadMbps"] = downMbps.ToString(),
                ["services:slskd:downloadSlots"] = dlSlots?.ToString(),
                ["services:slskd:uploadSlots"] = ulSlots?.ToString()
            })
            .Build();

    static VolumePathResolver Resolver() => new(new StorageConfig());

    [Test]
    public async Task BuildContainerSpecs_UsesExpectedIdentity()
    {
        ContainerSpec spec = new SlskdServiceDefinition()
            .BuildContainerSpecs(LimitsConfig(), Resolver(), CreateStackarr("abc")).Single();

        await Assert.That(spec.Image).IsEqualTo("slskd/slskd:latest");
        await Assert.That(spec.Name).IsEqualTo("stackarr-abc-slskd");
        await Assert.That(spec.Environment["SLSKD_HTTP_PORT"]).IsEqualTo("5030");
        await Assert.That(spec.Environment["SLSKD_REMOTE_CONFIGURATION"]).IsEqualTo("true");
        await Assert.That(spec.Labels[ContainerLabels.Service]).IsEqualTo("slskd");
    }

    [Test]
    public async Task BuildContainerSpecs_UsesConfiguredSlots()
    {
        ContainerSpec spec = new SlskdServiceDefinition()
            .BuildContainerSpecs(LimitsConfig(dlSlots: 12, ulSlots: 8), Resolver(), CreateStackarr()).Single();

        await Assert.That(spec.Environment["SLSKD_DOWNLOAD_SLOTS"]).IsEqualTo("12");
        await Assert.That(spec.Environment["SLSKD_UPLOAD_SLOTS"]).IsEqualTo("8");
    }

    [Test]
    public async Task BuildContainerSpecs_MapsSpeedLimits_AndOmitsUnlimited()
    {
        ContainerSpec spec = new SlskdServiceDefinition()
            .BuildContainerSpecs(LimitsConfig(upMbps: 4, downMbps: 0), Resolver(), CreateStackarr()).Single();

        // 4 MB/s → 4096 KiB/s; download is unlimited so the env var is left unset.
        await Assert.That(spec.Environment["SLSKD_UPLOAD_SPEED_LIMIT"]).IsEqualTo("4096");
        await Assert.That(spec.Environment.ContainsKey("SLSKD_DOWNLOAD_SPEED_LIMIT")).IsFalse();
    }

    [Test]
    public async Task Spec_DeclaresRequiredSlskdDirectories()
    {
        // slskd refuses to start unless its download directories already exist, and they are
        // subdirectories of the mounted /downloads root (not mount points themselves).
        ContainerSpec spec = new SlskdServiceDefinition()
            .BuildContainerSpecs(LimitsConfig(), Resolver(), CreateStackarr()).Single();

        await Assert.That(spec.RequiredDirectories).Contains("$downloads/slskd");
        await Assert.That(spec.RequiredDirectories).Contains("$downloads/slskd/incomplete");
        await Assert.That(spec.RequiredDirectories).Contains("$downloads/slskd/shares");
    }

    [Test]
    public async Task RequiredDirectories_ResolveUnderTheDownloadsMount()
    {
        VolumePathResolver resolver = Resolver();
        ContainerSpec spec = new SlskdServiceDefinition()
            .BuildContainerSpecs(LimitsConfig(), resolver, CreateStackarr()).Single();

        foreach (string directory in spec.RequiredDirectories)
        {
            await Assert.That(resolver.ResolveToContainerPath(directory)).StartsWith("/downloads/slskd");
        }
    }

    [Test]
    public async Task Spec_MountsDownloadsAndJoinsPerStackNetwork()
    {
        ContainerSpec spec = new SlskdServiceDefinition()
            .BuildContainerSpecs(LimitsConfig(), Resolver(), CreateStackarr()).Single();

        await Assert.That(spec.NetworkName).IsEqualTo("stackarr-test-net");

        VolumeMount downloads = spec.Volumes.Single(v => v.ContainerPath == "/downloads");
        await Assert.That(downloads.HostPath).IsEqualTo(RelativeVolumes.Downloads);
        await Assert.That(downloads.Type).IsEqualTo(VolumeType.Bind);
        await Assert.That(downloads.Permissions).IsEqualTo(VolumePerms.ReadWrite);
    }
}
