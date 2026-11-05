using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.TestSupport;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Models;
using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr.Tests;

/// <summary>
/// TV searches delegate to Sonarr's own commands. Sonarr owns release selection, queue awareness
/// and the season-pack-vs-episode decision; Stackarr only tells it what scope to search.
/// </summary>
public class SonarrSearchTests
{
    static SonarrService Sonarr(FakeHttpMessageHandler handler) =>
        new(new SonarrClient("http://localhost", "key", handler), NullLogger<SonarrService>.Instance);

    static FakeHttpMessageHandler Handler()
    {
        return new FakeHttpMessageHandler
        {
            Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("{}"))
        };
    }

    [Test]
    public async Task SeriesSearch_SendsNativeSeriesSearch()
    {
        FakeHttpMessageHandler handler = Handler();

        await Sonarr(handler).CommandSearchSeries(7);

        CapturedRequest command = handler.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/v3/command");
        await Assert.That(command.Body).Contains("SeriesSearch");
        await Assert.That(command.Body).Contains("\"seriesId\":7");
    }

    [Test]
    public async Task SeasonSearch_SendsNativeSeasonSearch()
    {
        FakeHttpMessageHandler handler = Handler();

        await Sonarr(handler).CommandSearchSeason(7, 2);

        CapturedRequest command = handler.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/v3/command");
        await Assert.That(command.Body).Contains("SeasonSearch");
        await Assert.That(command.Body).Contains("\"seasonNumber\":2");
    }

    [Test]
    public async Task EpisodeSearch_SendsNativeEpisodeSearch()
    {
        FakeHttpMessageHandler handler = Handler();

        await Sonarr(handler).CommandSearchEpisode(42);

        CapturedRequest command = handler.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/v3/command");
        await Assert.That(command.Body).Contains("EpisodeSearch");
        await Assert.That(command.Body).Contains("\"episodeIds\":[42]");
    }

    [Test]
    public async Task MissingSearch_SendsNativeMissingEpisodeSearch()
    {
        FakeHttpMessageHandler handler = Handler();

        await Sonarr(handler).SearchMissingAsync();

        CapturedRequest command = handler.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/v3/command");
        await Assert.That(command.Body).Contains("MissingEpisodeSearch");
    }

    [Test]
    public async Task InteractiveSearch_IsAPassThrough()
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("""
                [ { "guid": "release-1", "title": "Show S01 1080p", "indexerId": 1, "indexer": "Test", "rejected": false } ]
                """))
        };

        List<ReleaseResourceDto> releases = await Sonarr(handler).GetReleases(seriesId: 7, seasonNumber: 1);

        await Assert.That(releases.Count).IsEqualTo(1);
        CapturedRequest request = handler.Requests.Single();
        await Assert.That(request.Uri.Query).Contains("seriesId=7");
        await Assert.That(request.Uri.Query).Contains("seasonNumber=1");
    }
}
