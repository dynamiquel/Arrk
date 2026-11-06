using Arrk.Core.Configuration;
using Arrk.Core.Services;

namespace Arrk.Configuration.Tests;

/// <summary>
/// Covers how <see cref="StorageConfig"/> values are interpreted by
/// <see cref="VolumePathResolver"/>, the actual storage path resolution the app relies on.
/// </summary>
public class StorageConfigTests
{
    [Test]
    public async Task Resolver_RelativeMediaSubpath_CombinesWithMediaRoot()
    {
        VolumePathResolver resolver = new(new StorageConfig { Media = "/mnt/media", Tv = "$media/tv" });

        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.Tv)).IsEqualTo("/mnt/media/tv");
        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.TvShows)).IsEqualTo("/mnt/media/tv/shows");
    }

    [Test]
    public async Task Resolver_ConfiguredSubpaths_AreIndependent()
    {
        VolumePathResolver resolver = new(new StorageConfig
        {
            Media = "/data",
            Tv = "$media/shows-tv",
            Music = "$media/audio"
        });

        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.Tv)).IsEqualTo("/data/shows-tv");
        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.Music)).IsEqualTo("/data/audio");
    }
}
