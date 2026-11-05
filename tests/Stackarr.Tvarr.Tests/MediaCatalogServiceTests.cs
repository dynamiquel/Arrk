using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Tvarr.Components.Shared;
using Stackarr.Ui.Components;
using Stackarr.Tvarr.Services;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Services;
using Stackarr.TestSupport;

namespace Stackarr.Tvarr.Tests;

/// <summary>
/// Exercises the preferred-TMDB / fallback-Radarr+Sonarr catalog layer, including
/// library membership mapping and the anime seasonal discovery filters.
/// </summary>
public class MediaCatalogServiceTests
{
    sealed class Harness
    {
        public FakeHttpMessageHandler TmdbHandler { get; } = new();
        public FakeHttpMessageHandler RadarrHandler { get; } = new();
        public FakeHttpMessageHandler SonarrHandler { get; } = new();
        public MediaCatalogService Catalog { get; }

        public Harness(
            string? libraryMovies = null,
            string? librarySeries = null,
            string? movieLookup = null,
            string? seriesLookup = null,
            string? tmdbSearch = null,
            string? tmdbDiscover = null,
            string? tmdbTrending = null,
            string? tmdbGenre = null,
            string? tmdbTvDetail = null,
            bool tmdbConfigured = true)
        {
            TmdbService tmdb = new(
                tmdbConfigured ? "key" : "",
                tmdbConfigured ? "token" : "",
                null,
                TmdbHandler);

            RadarrHandler.Fallback = (req, _) => Task.FromResult(Route(req, libraryMovies, movieLookup));
            SonarrHandler.Fallback = (req, _) => Task.FromResult(Route(req, librarySeries, seriesLookup));

            RadarrClient radarrClient = new("http://radarr/", "k", RadarrHandler);
            SonarrClient sonarrClient = new("http://sonarr/", "k", SonarrHandler);
            RadarrService radarr = new(radarrClient, NullLogger<RadarrService>.Instance);
            SonarrService sonarr = new(sonarrClient, NullLogger<SonarrService>.Instance);

            TmdbHandler.Fallback = (req, _) => Task.FromResult(TmdbRoute(req, tmdbSearch, tmdbDiscover, tmdbTrending, tmdbGenre, tmdbTvDetail));

            Catalog = new MediaCatalogService(tmdb, radarr, sonarr, NullLogger<MediaCatalogService>.Instance);
        }

        static HttpResponseMessage Route(HttpRequestMessage req, string? library, string? lookup)
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/lookup", StringComparison.OrdinalIgnoreCase))
                return Json(lookup ?? "[]");
            return Json(library ?? "[]");
        }

        static HttpResponseMessage TmdbRoute(
            HttpRequestMessage req, string? search, string? discover, string? trending, string? genre, string? tvDetail)
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path.Contains("/genre/")) return Json(genre ?? """{"genres":[]}""");
            if (path.Contains("/search/")) return Json(search ?? """{"results":[]}""");
            if (path.Contains("/trending/")) return Json(trending ?? """{"results":[]}""");
            if (path.Contains("/discover/")) return Json(discover ?? """{"results":[]}""");
            if (path.Contains("/recommendations")) return Json(search ?? """{"results":[]}""");
            if (path.EndsWith("/images")) return Json("""{"logos":[{"file_path":"/logo.png","iso_639_1":"en","vote_average":5}]}""");
            if (path.StartsWith("/3/tv/")) return Json(tvDetail ?? "null");
            if (path.StartsWith("/3/movie/")) return Json("null");
            return Json("""{"results":[]}""");
        }

        static HttpResponseMessage Json(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public string? LastTmdbQuery => TmdbHandler.Requests.LastOrDefault()?.Uri.Query;
        public Uri? LastTmdbUri => TmdbHandler.Requests.LastOrDefault()?.Uri;
    }

    const string TmdbMovies = """
    {
      "results": [
        { "id": 603, "media_type": "movie", "title": "The Matrix", "release_date": "1999-03-31", "vote_average": 8.2, "genre_ids": [878] },
        { "id": 27205, "media_type": "movie", "title": "Inception", "release_date": "2010-07-16", "vote_average": 8.4, "genre_ids": [878] }
      ]
    }
    """;

    const string RadarrLibrary = """
    [
      { "id": 1, "title": "The Matrix", "tmdbId": 603, "hasFile": true, "monitored": true, "year": 1999, "sizeOnDisk": 8000000000, "genres": ["Science Fiction"], "images": [] }
    ]
    """;

    const string TmdbShows = """
    {
      "results": [
        { "id": 1396, "media_type": "tv", "name": "Breaking Bad", "first_air_date": "2008-01-20", "vote_average": 8.9, "genre_ids": [18], "origin_country": ["US"] }
      ]
    }
    """;

    const string SonarrLibrary = """
    [
      {
        "id": 5, "title": "Breaking Bad", "tmdbId": 1396, "tvdbId": 81189, "monitored": true, "year": 2008,
        "statistics": { "episodeFileCount": 62, "episodeCount": 62, "sizeOnDisk": 40000000000 },
        "seasons": [ { "seasonNumber": 1, "monitored": true, "statistics": { "episodeCount": 7, "episodeFileCount": 7 } } ]
      }
    ]
    """;

    // Membership mapping

    [Test]
    public async Task SearchMovies_MarksItemsAlreadyInLibrary()
    {
        Harness h = new(libraryMovies: RadarrLibrary, tmdbSearch: TmdbMovies);

        List<MediaItem> results = await h.Catalog.SearchMoviesAsync("matrix");

        await Assert.That(results.Count).IsEqualTo(2);
        MediaItem inLibrary = results.Single(r => r.TmdbId == 603);
        MediaItem remote = results.Single(r => r.TmdbId == 27205);

        await Assert.That(inLibrary.IsInLibrary).IsTrue();
        await Assert.That(inLibrary.HasFile).IsTrue();
        await Assert.That(inLibrary.LibraryStatus).IsEqualTo("Downloaded");
        await Assert.That(inLibrary.IsSearchResult).IsFalse();

        await Assert.That(remote.IsInLibrary).IsFalse();
        await Assert.That(remote.IsSearchResult).IsTrue();
        await Assert.That(remote.LibraryBadge).IsEqualTo("Not in Library");
    }

    [Test]
    public async Task SearchShows_MarksItemsAlreadyInLibrary()
    {
        Harness h = new(librarySeries: SonarrLibrary, tmdbSearch: TmdbShows);

        List<MediaItem> results = await h.Catalog.SearchShowsAsync("breaking bad");

        await Assert.That(results.Count).IsEqualTo(1);
        MediaItem show = results[0];
        await Assert.That(show.IsInLibrary).IsTrue();
        await Assert.That(show.MediaType).IsEqualTo("tv");
        // Sonarr season statistics are summed by the service mapper.
        await Assert.That(show.DownloadedEpisodes).IsEqualTo(7);
        await Assert.That(show.TotalEpisodes).IsEqualTo(7);
    }

    [Test]
    public async Task SearchMovies_UsesTmdb_WhenConfigured()
    {
        Harness h = new(libraryMovies: "[]", movieLookup: """[{"tmdbId":1,"title":"Fallback"}]""", tmdbSearch: TmdbMovies);

        List<MediaItem> results = await h.Catalog.SearchMoviesAsync("matrix");

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(h.TmdbHandler.Requests.Any(r => r.Uri.AbsolutePath.Contains("/search/movie"))).IsTrue();
        await Assert.That(h.RadarrHandler.Requests.Any(r => r.Uri.AbsolutePath.Contains("/lookup"))).IsFalse();
    }

    [Test]
    public async Task SearchMovies_FallsBackToRadarr_WhenTmdbNotConfigured()
    {
        Harness h = new(
            libraryMovies: "[]",
            movieLookup: """[{"tmdbId": 550, "title": "Fight Club", "year": 1999, "overview": "x"}]""",
            tmdbConfigured: false);

        List<MediaItem> results = await h.Catalog.SearchMoviesAsync("fight club");

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Title).IsEqualTo("Fight Club");
        await Assert.That(h.TmdbHandler.Requests.Count).IsEqualTo(0);
        await Assert.That(h.RadarrHandler.Requests.Any(r => r.Uri.AbsolutePath.Contains("/lookup"))).IsTrue();
    }

    [Test]
    public async Task SearchShows_FallsBackToSonarr_WhenTmdbNotConfigured()
    {
        Harness h = new(
            librarySeries: "[]",
            seriesLookup: """[{"tvdbId": 81189, "tmdbId": 1396, "title": "Breaking Bad", "year": 2008}]""",
            tmdbConfigured: false);

        List<MediaItem> results = await h.Catalog.SearchShowsAsync("breaking bad");

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Title).IsEqualTo("Breaking Bad");
        await Assert.That(h.SonarrHandler.Requests.Any(r => r.Uri.AbsolutePath.Contains("/lookup"))).IsTrue();
    }

    // Anime search filtering

    const string MixedTvSearch = """
    {
      "results": [
        { "id": 1, "media_type": "tv", "name": "Anime Show", "first_air_date": "2020-01-01", "genre_ids": [16, 10759], "origin_country": ["JP"], "original_language": "ja" },
        { "id": 2, "media_type": "tv", "name": "Drama Show", "first_air_date": "2020-01-01", "genre_ids": [18], "origin_country": ["US"], "original_language": "en" },
        { "id": 3, "media_type": "tv", "name": "Western Cartoon", "first_air_date": "2020-01-01", "genre_ids": [16], "origin_country": ["US"], "original_language": "en" }
      ]
    }
    """;

    [Test]
    public async Task SearchAnime_ReturnsOnlyJapaneseAnimation()
    {
        Harness h = new(librarySeries: "[]", tmdbSearch: MixedTvSearch);

        List<MediaItem> results = await h.Catalog.SearchAnimeAsync("show");

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Title).IsEqualTo("Anime Show");
        await Assert.That(results[0].IsAnime).IsTrue();
    }

    [Test]
    public async Task SearchAnime_IncludesChineseAndKorean_ExcludesWestern()
    {
        Harness h = new(librarySeries: "[]", tmdbSearch: """
        {
          "results": [
            { "id": 1, "media_type": "tv", "name": "Japanese Anime", "genre_ids": [16], "origin_country": ["JP"] },
            { "id": 2, "media_type": "tv", "name": "Chinese Donghua", "genre_ids": [16], "origin_country": ["CN"] },
            { "id": 3, "media_type": "tv", "name": "Korean Animation", "genre_ids": [16], "origin_country": ["KR"] },
            { "id": 4, "media_type": "tv", "name": "American Cartoon", "genre_ids": [16], "origin_country": ["US"] },
            { "id": 5, "media_type": "tv", "name": "British Cartoon", "genre_ids": [16], "origin_country": ["GB"] }
          ]
        }
        """);

        List<MediaItem> results = await h.Catalog.SearchAnimeAsync("animation");

        await Assert.That(results.Count).IsEqualTo(3);
        await Assert.That(results.Any(r => r.Title == "American Cartoon")).IsFalse();
        await Assert.That(results.Any(r => r.Title == "Chinese Donghua")).IsTrue();
        await Assert.That(results.Any(r => r.Title == "Korean Animation")).IsTrue();
    }

    // Movie list/search payloads omit origin_country, so the tag must fall back to the
    // original language instead of flagging every animated film as anime.
    const string MixedMovieSearch = """
    {
      "results": [
        { "id": 1, "media_type": "movie", "title": "Anime Movie", "genre_ids": [16], "original_language": "ja" },
        { "id": 2, "media_type": "movie", "title": "Western Cartoon Movie", "genre_ids": [16, 10751], "original_language": "en" },
        { "id": 3, "media_type": "movie", "title": "Live Action", "genre_ids": [18], "original_language": "en" },
        { "id": 4, "media_type": "movie", "title": "Chinese Animation", "genre_ids": [16], "origin_country": ["CN"], "original_language": "en" }
      ]
    }
    """;

    [Test]
    public async Task SearchMovies_TagsOnlyAsianAnimationAsAnime()
    {
        Harness h = new(libraryMovies: "[]", tmdbSearch: MixedMovieSearch);

        List<MediaItem> results = await h.Catalog.SearchMoviesAsync("movie");

        await Assert.That(results.Single(r => r.Title == "Anime Movie").IsAnime).IsTrue();
        await Assert.That(results.Single(r => r.Title == "Chinese Animation").IsAnime).IsTrue();
        await Assert.That(results.Single(r => r.Title == "Western Cartoon Movie").IsAnime).IsFalse();
        await Assert.That(results.Single(r => r.Title == "Live Action").IsAnime).IsFalse();
    }

    [Test]
    public async Task DiscoverMovies_DoesNotTagWesternCartoonsAsAnime()
    {
        Harness h = new(libraryMovies: "[]", tmdbDiscover: MixedMovieSearch);

        List<MediaItem> results = await h.Catalog.DiscoverAsync(MediaSection.Movies, null, null, null);

        await Assert.That(results.Single(r => r.Title == "Western Cartoon Movie").IsAnime).IsFalse();
        await Assert.That(results.Single(r => r.Title == "Anime Movie").IsAnime).IsTrue();
    }

    [Test]
    public async Task GetLogoUrl_FetchesOnceAndCaches()
    {
        Harness h = new();

        string first = await h.Catalog.GetLogoUrlAsync("movie", 603);
        string second = await h.Catalog.GetLogoUrlAsync("movie", 603);

        await Assert.That(first).Contains("/logo.png");
        await Assert.That(second).IsEqualTo(first);
        await Assert.That(h.TmdbHandler.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/images"))).IsEqualTo(1);
    }

    [Test]
    public async Task GetRecommendations_MapsWithMembership()
    {
        Harness h = new(libraryMovies: RadarrLibrary, tmdbSearch: TmdbMovies);

        List<MediaItem> results = await h.Catalog.GetRecommendationsAsync("movie", 603);

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results.Single(r => r.TmdbId == 603).IsInLibrary).IsTrue();
        await Assert.That(h.TmdbHandler.Requests.Any(r => r.Uri.AbsolutePath.Contains("/movie/603/recommendations"))).IsTrue();
    }

    [Test]
    public async Task SearchShows_DoesNotFilterAnime()
    {
        Harness h = new(librarySeries: "[]", tmdbSearch: MixedTvSearch);

        List<MediaItem> results = await h.Catalog.SearchShowsAsync("show");

        await Assert.That(results.Count).IsEqualTo(3);
    }

    [Test]
    public async Task SearchAsync_AnimeSection_UsesAnimeFilter()
    {
        Harness h = new(librarySeries: "[]", tmdbSearch: MixedTvSearch);

        List<MediaItem> results = await h.Catalog.SearchAsync(MediaSection.Anime, "show");

        await Assert.That(results.Count).IsEqualTo(1);
        await Assert.That(results[0].Title).IsEqualTo("Anime Show");
    }

    // Library lookup

    [Test]
    public async Task GetLibraryMovie_ReturnsFullLibraryEntry()
    {
        Harness h = new(libraryMovies: RadarrLibrary);

        Stackarr.Tv.Abstractions.TvMovie? movie = await h.Catalog.GetLibraryMovieAsync(603);

        await Assert.That(movie).IsNotNull();
        await Assert.That(movie!.Title).IsEqualTo("The Matrix");
        await Assert.That(movie.HasFile).IsTrue();
    }

    // Genres / discover

    [Test]
    public async Task Search_ResolvesGenreNamesFromGenreList()
    {
        Harness h = new(
            libraryMovies: "[]",
            tmdbGenre: """{"genres":[{"id":878,"name":"Science Fiction"}]}""",
            tmdbSearch: TmdbMovies);

        List<MediaItem> results = await h.Catalog.SearchMoviesAsync("matrix");

        await Assert.That(results[0].Genres).Contains("Science Fiction");
    }

    [Test]
    public async Task GetCategory_Movies_Trending_UsesTrendingEndpoint()
    {
        Harness h = new(libraryMovies: "[]", tmdbTrending: TmdbMovies);

        List<MediaItem> results = await h.Catalog.GetCategoryAsync(MediaSection.Movies, DiscoverCategory.Trending);

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(h.TmdbHandler.Requests.Any(r => r.Uri.AbsolutePath.Contains("/trending/movie"))).IsTrue();
    }

    [Test]
    public async Task GetCategory_ReturnsEmpty_WhenTmdbNotConfigured()
    {
        Harness h = new(tmdbConfigured: false);

        List<MediaItem> results = await h.Catalog.GetCategoryAsync(MediaSection.Movies, DiscoverCategory.Popular);

        await Assert.That(results.Count).IsEqualTo(0);
    }

    // Anime

    [Test]
    public async Task AnimeSeasonal_UsesAnimationJapanAndDateRange()
    {
        Harness h = new(librarySeries: "[]", tmdbDiscover: TmdbShows);

        await h.Catalog.GetAnimeSeasonalAsync("winter", 2024);

        Uri uri = h.TmdbHandler.Requests.Last(r => r.Uri.AbsolutePath.Contains("/discover/tv")).Uri;
        string query = uri.Query;
        await Assert.That(query).Contains("with_genres=16");
        await Assert.That(query).Contains("with_origin_country=JP");
        await Assert.That(query).Contains("without_genres=10762");
        await Assert.That(query).Contains("first_air_date.gte=2024-01-01");
        await Assert.That(query).Contains("first_air_date.lte=2024-03-31");
    }

    [Test]
    public async Task AnimeDiscover_AlwaysRestrictsToJapaneseAnimation()
    {
        Harness h = new(librarySeries: "[]", tmdbDiscover: TmdbShows);

        await h.Catalog.DiscoverAsync(MediaSection.Anime, null, null, null);

        Uri uri = h.TmdbHandler.Requests.Last(r => r.Uri.AbsolutePath.Contains("/discover/tv")).Uri;
        await Assert.That(uri.Query).Contains("with_origin_country=JP");
        await Assert.That(uri.Query).Contains("with_genres=16");
    }

    [Test]
    public async Task AnimeSeasonal_CurrentSeasonResolvesToValidRange()
    {
        Harness h = new(librarySeries: "[]", tmdbDiscover: TmdbShows);

        await h.Catalog.GetAnimeSeasonalAsync(null, 2025);

        Uri uri = h.TmdbHandler.Requests.Last(r => r.Uri.AbsolutePath.Contains("/discover/tv")).Uri;
        await Assert.That(uri.Query).Contains("first_air_date.gte=2025-");
        await Assert.That(uri.Query).Contains("first_air_date.lte=2025-");
    }

    // Add-to-library enrichment

    [Test]
    public async Task EnrichShow_FillsTvdbIdSeasonsAndNetwork()
    {
        Harness h = new(tmdbTvDetail: """
        {
          "id": 1396,
          "name": "Breaking Bad",
          "status": "Ended",
          "number_of_seasons": 5,
          "origin_country": ["US"],
          "genres": [{ "id": 18, "name": "Drama" }],
          "networks": [{ "id": 174, "name": "AMC" }],
          "external_ids": { "tvdb_id": 81189, "imdb_id": "tt0903747" },
          "seasons": [
            { "season_number": 0, "episode_count": 12 },
            { "season_number": 1, "episode_count": 7 },
            { "season_number": 2, "episode_count": 13 }
          ]
        }
        """);

        MediaItem item = new() { Title = "Breaking Bad", TmdbId = 1396, MediaType = "tv" };
        MediaItem enriched = await h.Catalog.EnrichShowAsync(item);

        await Assert.That(enriched.TvdbId).IsEqualTo(81189);
        await Assert.That(enriched.ImdbId).IsEqualTo("tt0903747");
        await Assert.That(enriched.AvailableSeasonNumbers).IsEquivalentTo([1, 2]);
        await Assert.That(enriched.Network).IsEqualTo("AMC");
        await Assert.That(enriched.SeriesStatus).IsEqualTo("Ended");
    }

    [Test]
    public async Task EnrichShow_TagsKoreanAnimationAsAnime()
    {
        Harness h = new(tmdbTvDetail: """
        {
          "id": 7000,
          "name": "Korean Animation",
          "origin_country": ["KR"],
          "genres": [{ "id": 16, "name": "Animation" }]
        }
        """);

        MediaItem item = new() { Title = "Korean Animation", TmdbId = 7000, MediaType = "tv" };
        MediaItem enriched = await h.Catalog.EnrichShowAsync(item);

        await Assert.That(enriched.IsAnime).IsTrue();
    }

    [Test]
    public async Task EnrichShow_DoesNotTagWesternAnimationAsAnime()
    {
        Harness h = new(tmdbTvDetail: """
        {
          "id": 7001,
          "name": "Western Cartoon",
          "origin_country": ["US"],
          "original_language": "en",
          "genres": [{ "id": 16, "name": "Animation" }]
        }
        """);

        MediaItem item = new() { Title = "Western Cartoon", TmdbId = 7001, MediaType = "tv" };
        MediaItem enriched = await h.Catalog.EnrichShowAsync(item);

        await Assert.That(enriched.IsAnime).IsFalse();
    }

    [Test]
    public async Task ResolveTvdbId_CachesResult()
    {
        Harness h = new(tmdbTvDetail: """{ "id": 1396, "external_ids": { "tvdb_id": 81189 } }""");

        int first = await h.Catalog.ResolveTvdbIdAsync(1396);
        int second = await h.Catalog.ResolveTvdbIdAsync(1396);

        await Assert.That(first).IsEqualTo(81189);
        await Assert.That(second).IsEqualTo(81189);
        await Assert.That(h.TmdbHandler.Requests.Count(r => r.Uri.AbsolutePath == "/3/tv/1396")).IsEqualTo(1);
    }
}
