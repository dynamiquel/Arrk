using Arrk.Core.Configuration;

namespace Arrk.Configuration.Tests;

/// <summary>
/// Covers mapping container-visible paths to the host paths Docker needs for bind mounts.
/// </summary>
public class HostPathMapperTests
{
    static HostPathMapper Mapper(params (string Host, string Container)[] mounts) =>
        new(mounts.Select(m => new HostPathMapper.MountMapping(m.Host, m.Container)).ToList());

    [Test]
    public async Task ToHostPath_WithNoMounts_ReturnsPathUnchanged()
    {
        HostPathMapper mapper = Mapper();

        await Assert.That(mapper.ToHostPath("/media/tv/movies")).IsEqualTo("/media/tv/movies");
        await Assert.That(mapper.ToHostPath("./media/tv")).IsEqualTo("./media/tv");
    }

    [Test]
    public async Task ToHostPath_MapsPathUnderMountToHost()
    {
        // Mirrors: -v /mnt/arrk/media:/media
        HostPathMapper mapper = Mapper(("/mnt/arrk/media", "/media"));

        await Assert.That(mapper.ToHostPath("/media/tv/movies")).IsEqualTo("/mnt/arrk/media/tv/movies");
    }

    [Test]
    public async Task ToHostPath_MapsExactMountRoot()
    {
        HostPathMapper mapper = Mapper(("/mnt/arrk/config", "/config"));

        await Assert.That(mapper.ToHostPath("/config")).IsEqualTo("/mnt/arrk/config");
    }

    [Test]
    public async Task ToHostPath_PrefersLongestMatchingMount()
    {
        HostPathMapper mapper = Mapper(
            ("/host/data", "/"),
            ("/mnt/arrk/media", "/media"));

        // /media/... must match the more specific /media mount, not the root mount.
        await Assert.That(mapper.ToHostPath("/media/tv")).IsEqualTo("/mnt/arrk/media/tv");
        // Other paths fall through to the root mount.
        await Assert.That(mapper.ToHostPath("/config/arrk.yaml")).IsEqualTo("/host/data/config/arrk.yaml");
    }

    [Test]
    public async Task ToHostPath_DoesNotMapSiblingPathWithSamePrefix()
    {
        HostPathMapper mapper = Mapper(("/mnt/arrk/media", "/media"));

        // "/media-library" is not under "/media".
        await Assert.That(mapper.ToHostPath("/media-library/films")).IsEqualTo("/media-library/films");
    }
}
