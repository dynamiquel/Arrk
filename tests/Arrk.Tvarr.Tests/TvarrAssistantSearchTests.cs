using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Ai;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Services;
using Arrk.TestSupport;

namespace Arrk.Tvarr.Tests;

/// <summary>
/// Structured assistant search over the TV/movie catalogue: library genre/anime filtering, TMDB
/// person credits and discovery ordering. These run against the real catalog layer with stubbed
/// Radarr/Sonarr/TMDB HTTP, so the filters are exercised end to end without a network.
/// </summary>
public class TvarrAssistantSearchTests
{
    const string RadarrLibrary = """
    [
      { "id": 1, "title": "The Matrix", "tmdbId": 603, "hasFile": true, "monitored": true, "year": 1999,
        "genres": ["Action", "Science Fiction"], "originalLanguage": { "id": 1, "name": "English" }, "images": [] },
      { "id": 2, "title": "Spirited Away", "tmdbId": 129, "hasFile": true, "monitored": true, "year": 2001,
        "genres": ["Animation", "Family"], "originalLanguage": { "id": 2, "name": "Japanese" }, "images": [] },
      { "id": 3, "title": "Inception", "tmdbId": 27205, "hasFile": false, "monitored": true, "year": 2010,
        "genres": ["Action", "Science Fiction"], "originalLanguage": { "id": 1, "name": "English" }, "images": [] }
    ]
    """;

    const string People = """
    { "results": [ { "id": 500, "name": "Tom Cruise", "known_for_department": "Acting" } ] }
    """;

    const string Credits = """
    {
      "cast": [
        { "id": 1, "media_type": "movie", "title": "Older Film", "release_date": "2005-01-01", "popularity": 10 },
        { "id": 2, "media_type": "movie", "title": "New Film", "release_date": "2024-05-01", "popularity": 5 }
      ]
    }
    """;

    sealed class Harness
    {
        readonly FakeHttpMessageHandler _tmdbHandler = new();
        public TvarrAssistantMediaSource Source { get; }

        public Harness()
        {
            FakeHttpMessageHandler radarrHandler = new();
            FakeHttpMessageHandler sonarrHandler = new();

            radarrHandler.Fallback = (req, _) => Task.FromResult(Json(
                req.RequestUri!.AbsolutePath.EndsWith("/lookup", StringComparison.OrdinalIgnoreCase)
                    ? "[]"
                    : RadarrLibrary));
            sonarrHandler.Fallback = (_, _) => Task.FromResult(Json("[]"));
            _tmdbHandler.Fallback = (req, _) => Task.FromResult(Route(req.RequestUri!.AbsolutePath));

            TmdbService tmdb = new("key", "token", null, _tmdbHandler);
            RadarrService radarr = new(new RadarrClient("http://radarr/", "k", radarrHandler),
                NullLogger<RadarrService>.Instance);
            SonarrService sonarr = new(new SonarrClient("http://sonarr/", "k", sonarrHandler),
                NullLogger<SonarrService>.Instance);
            MediaCatalogService catalog = new(tmdb, radarr, sonarr, NullLogger<MediaCatalogService>.Instance);

            Source = new TvarrAssistantMediaSource(
                catalog, sonarr, tmdb, new Arrk.Core.Playback.WatchStateService([]),
                NullLogger<TvarrAssistantMediaSource>.Instance);
        }

        static HttpResponseMessage Route(string path)
        {
            if (path.Contains("/search/person", StringComparison.OrdinalIgnoreCase)) return Json(People);
            if (path.Contains("/combined_credits", StringComparison.OrdinalIgnoreCase)) return Json(Credits);
            if (path.Contains("/genre/", StringComparison.OrdinalIgnoreCase)) return Json("""{"genres":[]}""");
            return Json("""{"results":[]}""");
        }

        static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    [Test]
    public async Task GenreSearch_WorksWithoutText_AndNormalisesAliases()
    {
        Harness harness = new();

        IReadOnlyList<AssistantMediaSummary> results = await harness.Source.SearchAsync(
            new AssistantMediaQuery("", "movie", 10, null,
                Genre: "sci-fi", Scope: AssistantMediaScope.Library), default);

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results.Any(r => r.Title == "The Matrix")).IsTrue();
        await Assert.That(results.Any(r => r.Title == "Inception")).IsTrue();
        await Assert.That(results.Any(r => r.Title == "Spirited Away")).IsFalse();
    }

    [Test]
    public async Task AnimeSearch_ReturnsOnlyJapaneseAnimation()
    {
        Harness harness = new();

        IReadOnlyList<AssistantMediaSummary> results = await harness.Source.SearchAsync(
            new AssistantMediaQuery("", "anime", 10, null,
                Scope: AssistantMediaScope.Library, Anime: true), default);

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Title).IsEqualTo("Spirited Away");
    }

    [Test]
    public async Task PersonDiscovery_UsesTmdbCredits_AndSortsNewestFirst()
    {
        Harness harness = new();

        IReadOnlyList<AssistantMediaSummary> results = await harness.Source.SearchAsync(
            new AssistantMediaQuery("", "movie", 10, null,
                Person: "Tom Cruise", Scope: AssistantMediaScope.Discovery, Sort: "newest"), default);

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results[0].Title).IsEqualTo("New Film");
        await Assert.That(results[0].Status).IsEqualTo("Not in library");
    }

    [Test]
    public async Task DiscoveryScope_ExcludesWhatTheUserAlreadyOwns()
    {
        Harness harness = new();

        IReadOnlyList<AssistantMediaSummary> library = await harness.Source.SearchAsync(
            new AssistantMediaQuery("", "movie", 10, null, Scope: AssistantMediaScope.Library), default);
        IReadOnlyList<AssistantMediaSummary> discovery = await harness.Source.SearchAsync(
            new AssistantMediaQuery("", "movie", 10, null, Scope: AssistantMediaScope.Discovery), default);

        await Assert.That(library.Count).IsEqualTo(3);
        await Assert.That(discovery.Any(r => r.Status != "Not in library")).IsFalse();
    }
}
