using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Models;

namespace Stackarr.Tvarr.Tests;

/// <summary>
/// Optional live tests against a real Radarr/Sonarr interactive-search endpoint. They only run when
/// <c>STACKARR_RADARR_URL</c> / <c>STACKARR_SONARR_URL</c> are set (API key via
/// <c>STACKARR_RADARR_API_KEY</c> / <c>STACKARR_SONARR_API_KEY</c>, defaulting to the dev Stackarr
/// key), so the normal suite stays hermetic. Set <c>STACKARR_SKIP_LIVE_TESTS=1</c> to force-skip.
///
/// These deliberately call the real HTTP path rather than a fake transport: they prove the release
/// DTOs deserialize the arrs' real payloads. They never grab a release (no downloads are started).
/// </summary>
public class ArrReleaseLiveTests
{
    static bool Skip() => Environment.GetEnvironmentVariable("STACKARR_SKIP_LIVE_TESTS") == "1";

    static RadarrClient? Radarr()
    {
        string? url = Environment.GetEnvironmentVariable("STACKARR_RADARR_URL");
        if (string.IsNullOrWhiteSpace(url))
            return null;
        string key = Environment.GetEnvironmentVariable("STACKARR_RADARR_API_KEY") ?? "stackarr";
        return new RadarrClient(url, key);
    }

    static SonarrClient? Sonarr()
    {
        string? url = Environment.GetEnvironmentVariable("STACKARR_SONARR_URL");
        if (string.IsNullOrWhiteSpace(url))
            return null;
        string key = Environment.GetEnvironmentVariable("STACKARR_SONARR_API_KEY") ?? "stackarr";
        return new SonarrClient(url, key);
    }

    [Test]
    public async Task Radarr_Live_InteractiveSearch_ParsesRealReleases()
    {
        if (Skip())
            return;
        RadarrClient? radarr = Radarr();
        if (radarr is null)
            return;

        List<MovieDto> movies = await radarr.GetMovies();
        if (movies.Count == 0)
            return;

        List<ReleaseResourceDto> releases = await radarr.GetReleases(movies[0].Id);

        await Assert.That(releases).IsNotNull();
        if (releases.Count == 0)
            return; // no indexers configured / nothing available — nothing to parse

        ReleaseResourceDto release = releases[0];
        await Assert.That(release.Title).IsNotEmpty();
        await Assert.That(release.Indexer).IsNotEmpty();
        await Assert.That(release.Quality?.Quality?.Name).IsNotNull();
        await Assert.That(release.Rejections).IsNotNull();
    }

    [Test]
    public async Task Sonarr_Live_InteractiveSearch_ParsesRealReleases()
    {
        if (Skip())
            return;
        SonarrClient? sonarr = Sonarr();
        if (sonarr is null)
            return;

        List<SeriesDto> series = await sonarr.GetSeries();
        if (series.Count == 0)
            return;

        List<ReleaseResourceDto> releases = await sonarr.GetReleases(seriesId: series[0].Id);

        await Assert.That(releases).IsNotNull();
        if (releases.Count == 0)
            return;

        ReleaseResourceDto release = releases[0];
        await Assert.That(release.Title).IsNotEmpty();
        await Assert.That(release.Indexer).IsNotEmpty();
        await Assert.That(release.Quality?.Quality?.Name).IsNotNull();
    }
}
