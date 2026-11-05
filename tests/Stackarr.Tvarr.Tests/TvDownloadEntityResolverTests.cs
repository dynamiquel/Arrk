using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Media.Abstractions;
using Stackarr.TestSupport;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr.Tests;

/// <summary>
/// Verifies the hash-to-media mapping used for subscriber-targeted download events. The resolver
/// reads the real arr queue shapes through the normal clients and a scripted transport.
/// </summary>
public class TvDownloadEntityResolverTests
{
    static TvDownloadEntityResolver Resolver(
        string radarrQueue,
        string sonarrQueue,
        string movie = "{}",
        string series = "{}",
        string episodes = "[]",
        FakeEventStore? store = null)
    {
        FakeEventStore eventStore = store ?? new FakeEventStore();
        FakeHttpMessageHandler radarrHandler = new()
        {
            Fallback = (request, _) =>
            {
                string path = request.RequestUri!.AbsolutePath;
                string response = path.StartsWith("/api/v3/movie/", StringComparison.OrdinalIgnoreCase)
                    ? movie
                    : radarrQueue;
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(response));
            }
        };
        FakeHttpMessageHandler sonarrHandler = new()
        {
            Fallback = (request, _) =>
            {
                string path = request.RequestUri!.AbsolutePath;
                string response = path switch
                {
                    _ when path.StartsWith("/api/v3/series/", StringComparison.OrdinalIgnoreCase) => series,
                    _ when path.StartsWith("/api/v3/episode", StringComparison.OrdinalIgnoreCase) => episodes,
                    _ => sonarrQueue
                };
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse(response));
            }
        };

        RadarrService radarr = new(
            new RadarrClient("http://localhost", "key", radarrHandler),
            NullLogger<RadarrService>.Instance,
            eventStore);
        SonarrService sonarr = new(
            new SonarrClient("http://localhost", "key", sonarrHandler),
            NullLogger<SonarrService>.Instance,
            eventStore);

        return new TvDownloadEntityResolver(
            radarr,
            sonarr,
            TimeProvider.System,
            NullLogger<TvDownloadEntityResolver>.Instance);
    }

    [Test]
    public async Task Radarr_queue_resolves_a_movie_entity()
    {
        const string queue = """
            { "page": 1, "pageSize": 10, "totalRecords": 1, "records": [
              { "id": 1, "movieId": 18, "downloadId": "hash1", "title": "Batman" } ] }
            """;
        const string movie = """
            { "id": 18, "title": "Batman Knightfall", "year": 2026, "tmdbId": 100,
              "images": [] }
            """;

        MediaEntityRef? entity = await Resolver(queue, "{}", movie).ResolveAsync("hash1");

        await Assert.That(entity).IsNotNull();
        await Assert.That(entity!.ProviderKey).IsEqualTo("tv");
        await Assert.That(entity.ItemKey).IsEqualTo("tv:movie:tmdb:100");
        await Assert.That(entity.Title).IsEqualTo("Batman Knightfall");
    }

    [Test]
    public async Task Sonarr_queue_resolves_an_episode_with_season_and_series_ancestors()
    {
        const string queue = """
            { "page": 1, "pageSize": 10, "totalRecords": 1, "records": [
              { "id": 1, "seriesId": 9, "episodeId": 33, "downloadId": "hash-ep",
                "title": "Severance S02E05" } ] }
            """;
        const string series = """
            { "id": 9, "title": "Severance", "tvdbId": 500, "tmdbId": 77, "images": [] }
            """;
        const string episodes = """
            [ { "id": 33, "seasonNumber": 2, "episodeNumber": 5, "hasFile": false } ]
            """;

        MediaEntityRef? entity = await Resolver("{}", queue, "{}", series, episodes).ResolveAsync("hash-ep");

        await Assert.That(entity).IsNotNull();
        await Assert.That(entity!.ItemKey).IsEqualTo("tv:series:tvdb:500:s2e5");
        await Assert.That(entity.Ancestors).Contains("tv:series:tvdb:500:s2");
        await Assert.That(entity.Ancestors).Contains("tv:series:tvdb:500");
        await Assert.That(entity.Subtitle).IsEqualTo("S02E05");
    }

    [Test]
    public async Task Unknown_hash_has_no_entity()
    {
        MediaEntityRef? entity = await Resolver("{}", "{}").ResolveAsync("missing");

        await Assert.That(entity).IsNull();
    }
}
