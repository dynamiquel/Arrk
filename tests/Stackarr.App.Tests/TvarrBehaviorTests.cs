using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Services;
using Stackarr.TestSupport;

namespace Stackarr.App.Tests;

/// <summary>
/// Behaviour that the new UI relies on: nested rating parsing and automatic search
/// after a movie/show is added.
/// </summary>
public class TvarrBehaviorTests
{
    static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static HttpResponseMessage Route(HttpRequestMessage req, string movieList, string seriesList)
    {
        string path = req.RequestUri!.AbsolutePath;
        if (path.EndsWith("/qualityprofile")) return Json("""[{"id":1,"name":"Any"}]""");
        if (path.EndsWith("/rootfolder")) return Json("""[{"id":1,"path":"/media"}]""");
        if (path.EndsWith("/episode"))
            return Json("""[{"id":77,"seriesId":7,"seasonNumber":1,"episodeNumber":1,"monitored":true,"hasFile":false,"airDateUtc":"2020-01-01T00:00:00Z"}]""");
        if (path.EndsWith("/queue")) return Json("""{"records":[]}""");
        if (path.EndsWith("/movie") && req.Method == HttpMethod.Get) return Json(movieList);
        if (path.EndsWith("/series") && req.Method == HttpMethod.Get) return Json(seriesList);
        if (path.EndsWith("/movie")) return Json("""{"id":42,"title":"Dune","tmdbId":438631}""");
        if (path.EndsWith("/series")) return Json("""{"id":7,"title":"Breaking Bad","tvdbId":81189,"seasons":[]}""");
        if (path.Contains("/command")) return Json("{}");
        return Json("{}");
    }

    static RadarrService Radarr(FakeHttpMessageHandler handler, string movies = "[]")
    {
        handler.Fallback = (req, _) => Task.FromResult(Route(req, movies, "[]"));
        RadarrClient client = new("http://radarr/", "k", handler);
        return new(client, NullLogger<RadarrService>.Instance);
    }

    static SonarrService Sonarr(FakeHttpMessageHandler handler, string series = "[]")
    {
        handler.Fallback = (req, _) => Task.FromResult(Route(req, "[]", series));
        SonarrClient client = new("http://sonarr/", "k", handler);
        return new(client, NullLogger<SonarrService>.Instance);
    }

    // Ratings: Radarr/Sonarr return nested objects

    [Test]
    public async Task RadarrLibrary_UsesNestedTmdbRating()
    {
        const string movies = """
        [
          {
            "id": 1, "title": "Dune", "tmdbId": 438631, "year": 2021,
            "ratings": { "imdb": { "value": 8.0, "votes": 900 }, "tmdb": { "value": 7.8, "votes": 12000 } },
            "images": []
          }
        ]
        """;
        FakeHttpMessageHandler handler = new();
        RadarrService service = Radarr(handler, movies);

        List<Stackarr.Tv.Abstractions.TvMovie> result = await service.GetAllMovies();

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Rating).IsEqualTo(7.8);
    }

    [Test]
    public async Task RadarrLibrary_FallsBackToImdb_WhenNoTmdbRating()
    {
        const string movies = """
        [ { "id": 1, "title": "Dune", "tmdbId": 1, "ratings": { "imdb": { "value": 8.0 } }, "images": [] } ]
        """;
        FakeHttpMessageHandler handler = new();
        RadarrService service = Radarr(handler, movies);

        List<Stackarr.Tv.Abstractions.TvMovie> result = await service.GetAllMovies();

        await Assert.That(result[0].Rating).IsEqualTo(8.0);
    }

    [Test]
    public async Task SonarrLibrary_UsesNestedTmdbRating()
    {
        const string series = """
        [
          {
            "id": 5, "title": "Breaking Bad", "tmdbId": 1396, "tvdbId": 81189, "year": 2008,
            "ratings": { "tmdb": { "value": 8.9, "votes": 5000 } },
            "seasons": [], "images": []
          }
        ]
        """;
        FakeHttpMessageHandler handler = new();
        SonarrService service = Sonarr(handler, series);

        List<Stackarr.Tv.Abstractions.TvSeries> result = await service.GetAllSeries();

        await Assert.That(result[0].Rating).IsEqualTo(8.9);
    }

    // Auto-search after add

    [Test]
    public async Task SearchAndAddMovie_TriggersMoviesSearch()
    {
        FakeHttpMessageHandler handler = new();
        RadarrService service = Radarr(handler);

        await service.SearchAndAddMovie(438631);

        CapturedRequest command = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/command"));
        await Assert.That(command.Body).Contains("MoviesSearch");
        await Assert.That(command.Body).Contains("42");
    }

    [Test]
    public async Task SearchAndAddMovie_CanSkipInitialSearch()
    {
        FakeHttpMessageHandler handler = new();
        RadarrService service = Radarr(handler);

        await service.SearchAndAddMovie(438631, searchForMovie: false);

        await Assert.That(handler.Requests.Any(r => r.Uri.AbsolutePath.EndsWith("/command"))).IsFalse();
    }

    [Test]
    public async Task AddSeriesWithOptions_TriggersSeriesSearch()
    {
        FakeHttpMessageHandler handler = new();
        SonarrService service = Sonarr(handler);

        await service.AddSeriesWithOptions(81189, "Breaking Bad", 1, monitoredSeasons: null, monitorFutureSeasons: true);

        CapturedRequest command = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/command"));
        await Assert.That(command.Body).Contains("SeriesSearch");
        await Assert.That(command.Body).Contains("\"seriesId\":7");
    }

    [Test]
    public async Task AddSeriesWithOptions_CanSkipInitialSearch()
    {
        FakeHttpMessageHandler handler = new();
        SonarrService service = Sonarr(handler);

        await service.AddSeriesWithOptions(81189, "Breaking Bad", 1, monitoredSeasons: null, monitorFutureSeasons: true, searchForSeries: false);

        await Assert.That(handler.Requests.Any(r => r.Uri.AbsolutePath.EndsWith("/command"))).IsFalse();
    }
}
