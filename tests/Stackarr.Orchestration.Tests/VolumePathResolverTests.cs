using Stackarr.Core.Configuration;
using Stackarr.Core.Services;

namespace Stackarr.Orchestration.Tests;

public class VolumePathResolverTests
{
    static VolumePathResolver Create(StorageConfig storage) => new(storage);

    [Test]
    public async Task ResolveToHostPath_Tv_CombinesTvUnderMedia()
    {
        VolumePathResolver resolver = Create(new() { Media = "/mnt/media", Tv = "$media/tv" });

        string path = resolver.ResolveToHostPath(RelativeVolumes.Tv);

        await Assert.That(path).IsEqualTo("/mnt/media/tv");
    }

    [Test]
    public async Task ResolveToHostPath_TvMovies_AppendsMoviesToTv()
    {
        VolumePathResolver resolver = Create(new() { Media = "/mnt/media", Tv = "$media/tv" });

        string path = resolver.ResolveToHostPath(RelativeVolumes.TvMovies);

        await Assert.That(path).IsEqualTo("/mnt/media/tv/movies");
    }

    [Test]
    public async Task ResolveToHostPath_TvShows_AppendsShowsToTv()
    {
        VolumePathResolver resolver = Create(new() { Media = "/mnt/media", Tv = "$media/tv" });

        string path = resolver.ResolveToHostPath(RelativeVolumes.TvShows);

        await Assert.That(path).IsEqualTo("/mnt/media/tv/shows");
    }

    [Test]
    public async Task ResolveToHostPath_Music_UsesItsOwnRoot()
    {
        VolumePathResolver resolver = Create(new()
        {
            Media = "/mnt/media",
            Music = "$media/music"
        });

        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.Music)).IsEqualTo("/mnt/media/music");
    }

    [Test]
    public async Task ResolveToHostPath_Downloads_UsesConfiguredDownloads()
    {
        VolumePathResolver resolver = Create(new() { Downloads = "/mnt/downloads" });

        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.Downloads)).IsEqualTo("/mnt/downloads");
    }

    [Test]
    public async Task ResolveToHostPath_Config_SubstitutesConfigRoot()
    {
        VolumePathResolver resolver = Create(new() { Config = "/mnt/stackarr/config" });

        string path = resolver.ResolveToHostPath("$config:radarr");

        await Assert.That(path).IsEqualTo("/mnt/stackarr/config/radarr");
    }

    [Test]
    public async Task ResolveToHostPath_DockerSocket_ReturnsHostSocketPath()
    {
        VolumePathResolver resolver = Create(new());

        string path = resolver.ResolveToHostPath(RelativeVolumes.DockerSocket);

        await Assert.That(path).IsEqualTo(Environment.GetEnvironmentVariable("DOCKER_SOCKET") ?? "/var/run/docker.sock");
    }

    [Test]
    public async Task ResolveToHostPath_AbsolutePath_IsReturnedUnchanged()
    {
        VolumePathResolver resolver = Create(new());

        await Assert.That(resolver.ResolveToHostPath("/absolute/path")).IsEqualTo("/absolute/path");
    }

    [Test]
    public async Task ResolveToContainerPath_MapsCoresToContainerRoots()
    {
        VolumePathResolver resolver = Create(new()
        {
            Config = "/mnt/stackarr/config",
            Downloads = "/mnt/downloads",
            Media = "/mnt/media",
            Tv = "$media/tv"
        });

        await Assert.That(resolver.ResolveToContainerPath("$config:radarr")).IsEqualTo("/config/radarr");
        await Assert.That(resolver.ResolveToContainerPath(RelativeVolumes.Downloads)).IsEqualTo("/downloads");
        await Assert.That(resolver.ResolveToContainerPath(RelativeVolumes.TvMovies)).IsEqualTo("/media/tv/movies");
    }

    [Test]
    public async Task RelativeVolumes_IsRelativeVolume_DetectsDollarPrefix()
    {
        await Assert.That(RelativeVolumes.IsRelativeVolume("$media/tv")).IsTrue();
        await Assert.That(RelativeVolumes.IsRelativeVolume("/mnt/media/tv")).IsFalse();
    }

    [Test]
    public async Task ResolveToHostPath_InContainer_MapsContainerRootsToHostMounts()
    {
        // Simulates the container: STACKARR_DATA_ROUTE=/ and host mounts
        // /mnt/stackarr/{media,config,downloads} into /media, /config, /downloads.
        HostPathMapper mapper = new(
        [
            new("/mnt/stackarr/media", "/media"),
            new("/mnt/stackarr/config", "/config"),
            new("/mnt/stackarr/downloads", "/downloads")
        ]);
        VolumePathResolver resolver = new(new StorageConfig(), dataRoot: "/", hostPathMapper: mapper);

        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.TvMovies))
            .IsEqualTo("/mnt/stackarr/media/tv/movies");
        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.Downloads))
            .IsEqualTo("/mnt/stackarr/downloads");
        await Assert.That(resolver.ResolveToHostPath("$config:prowlarr"))
            .IsEqualTo("/mnt/stackarr/config/prowlarr");
    }

    [Test]
    public async Task ResolveToLocalPath_InContainer_KeepsContainerPaths()
    {
        HostPathMapper mapper = new([new("/mnt/stackarr/media", "/media")]);
        VolumePathResolver resolver = new(new StorageConfig(), dataRoot: "/", hostPathMapper: mapper);

        // Local path is the mount-backed path used from inside Stackarr's container.
        await Assert.That(resolver.ResolveToLocalPath(RelativeVolumes.TvMovies)).IsEqualTo("/media/tv/movies");
    }

    [Test]
    public async Task ResolveToHostPath_Standalone_KeepsRelativeDataRoutePaths()
    {
        HostPathMapper identity = new([]);
        VolumePathResolver resolver = new(new StorageConfig(), dataRoot: "/work", hostPathMapper: identity);

        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.TvMovies))
            .IsEqualTo("/work/media/tv/movies");
        await Assert.That(resolver.ResolveToLocalPath(RelativeVolumes.TvMovies))
            .IsEqualTo("/work/media/tv/movies");
    }

    [Test]
    public async Task ResolveToHostPath_AbsoluteHostStorage_IsUsedVerbatimEvenInContainer()
    {
        // Config that declares an explicit host path must be handed to Docker as-is.
        HostPathMapper mapper = new([new("/mnt/stackarr/media", "/media")]);
        VolumePathResolver resolver = new(
            new StorageConfig
            {
                Media = "/mnt/stackarr/media",
                Downloads = "/mnt/stackarr/downloads",
                Config = "/mnt/stackarr/config"
            },
            dataRoot: "/",
            hostPathMapper: mapper);

        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.TvMovies))
            .IsEqualTo("/mnt/stackarr/media/tv/movies");
        await Assert.That(resolver.ResolveToHostPath(RelativeVolumes.Downloads))
            .IsEqualTo("/mnt/stackarr/downloads");
    }
}
