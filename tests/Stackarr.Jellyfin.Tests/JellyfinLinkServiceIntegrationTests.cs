using Stackarr.Core.Models;
using Stackarr.Jellyfin;

namespace Stackarr.Jellyfin.Tests;

/// <summary>
/// Deep-link resolution against a real Jellyfin container that has indexed tiny generated media
/// files. Skipped automatically when Docker (or ffmpeg for media generation) is unavailable.
/// </summary>
[NotInParallel(JellyfinServerShared.ParallelKey)]
public class JellyfinLinkServiceIntegrationTests
{
    static Task<JellyfinServer?> ServerAsync() => JellyfinServerShared.GetServerAsync();

    [Test]
    public async Task GetMovieUrl_ResolvesRealIndexedMovie()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null || !server.HasMedia) return;

        JellyfinItemsResult items = await server.WaitForItemsAsync("Movie", TimeSpan.FromMinutes(3));
        JellyfinItem? movie = items.Items.FirstOrDefault(i =>
            i.Path is not null && i.Path.Contains(JellyfinServer.TmdbMovieId.ToString(), StringComparison.Ordinal));
        if (movie is null) return;

        JellyfinLinkService service = server.CreateLinkService();
        TvMovie stackarrMovie = new()
        {
            Title = movie.Name,
            TmdbId = JellyfinServer.TmdbMovieId,
            HasFile = true,
            File = new MediaFileInfo { Path = movie.Path! }
        };

        string? url = await service.GetMovieUrlAsync(stackarrMovie);

        await Assert.That(url).IsEqualTo($"{service.BaseUrl}/web/#/details?id={movie.Id}");
    }

    [Test]
    public async Task GetMovieUrl_ReturnsNull_WhenNotIndexed()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinLinkService service = server.CreateLinkService();
        TvMovie movie = new()
        {
            Title = "Definitely Not In This Library XYZ",
            TmdbId = 99999999,
            HasFile = true,
            File = new MediaFileInfo { Path = "/movies/99999999/Definitely Not In This Library XYZ.mkv" }
        };

        string? url = await service.GetMovieUrlAsync(movie);

        await Assert.That(url).IsNull();
    }

    [Test]
    public async Task GetSeriesLinks_ResolvesRealIndexedSeriesAndEpisodes()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null || !server.HasMedia) return;

        JellyfinItemsResult items = await server.WaitForItemsAsync("Series", TimeSpan.FromMinutes(3));
        JellyfinItem? series = items.Items.FirstOrDefault();
        if (series is null) return;

        JellyfinLinkService service = server.CreateLinkService();
        TvSeries stackarrSeries = new()
        {
            Title = series.Name,
            TvdbId = JellyfinServer.TvdbSeriesId
        };

        TvSeriesLinks? links = await service.GetSeriesLinksAsync(stackarrSeries);

        await Assert.That(links).IsNotNull();
        await Assert.That(links!.SeriesUrl).IsEqualTo($"{service.BaseUrl}/web/#/details?id={series.Id}");
        await Assert.That(links.Episodes.ContainsKey((1, 1))).IsTrue();
        await Assert.That(links.Episodes.ContainsKey((1, 2))).IsTrue();
    }

    [Test]
    public async Task GetSeriesLinks_ReturnsNull_WhenNotIndexed()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        JellyfinLinkService service = server.CreateLinkService();
        TvSeries series = new() { Title = "Definitely Not A Series XYZ", TvdbId = 99999999 };

        TvSeriesLinks? links = await service.GetSeriesLinksAsync(series);

        await Assert.That(links).IsNull();
    }
}
