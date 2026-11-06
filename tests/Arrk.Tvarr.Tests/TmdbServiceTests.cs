using System.Net;
using Arrk.Tvarr.Services;
using Arrk.TestSupport;

namespace Arrk.Tvarr.Tests;

/// <summary>
/// Thorough coverage of the TMDB client: auth, request shaping, response parsing,
/// error handling and image URL construction.
/// </summary>
public class TmdbServiceTests
{
    const string Token = "eyJ.test.read.token";
    const string ApiKey = "test-api-key";

    static TmdbService Service(FakeHttpMessageHandler handler, string? apiKey = ApiKey, string? readToken = null)
        => new(apiKey ?? "", readToken ?? "", null, handler);

    static string? Param(Uri uri, string key)
    {
        string query = uri.Query.TrimStart('?');
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string k = eq < 0 ? pair : pair[..eq];
            if (Uri.UnescapeDataString(k) == key)
                return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    // Configuration / auth

    [Test]
    public async Task IsConfigured_False_WhenNoCredentials()
    {
        TmdbService service = new("", "", null, new FakeHttpMessageHandler());
        await Assert.That(service.IsConfigured).IsFalse();
    }

    [Test]
    public async Task IsConfigured_True_WithApiKey()
    {
        TmdbService service = new(ApiKey, "", null, new FakeHttpMessageHandler());
        await Assert.That(service.IsConfigured).IsTrue();
    }

    [Test]
    public async Task IsConfigured_True_WithReadToken()
    {
        TmdbService service = new("", Token, null, new FakeHttpMessageHandler());
        await Assert.That(service.IsConfigured).IsTrue();
    }

    [Test]
    public async Task Credentials_AreResolvedPerRequest_SoConfigEditsApply()
    {
        FakeHttpMessageHandler handler = new();
        string apiKey = "first-key";
        TmdbService service = new(() => (apiKey, ""), null, handler);
        handler.EnqueueJson("""{"page":1,"results":[],"total_pages":1,"total_results":0}""");

        await service.SearchMoviesAsync("dune");

        apiKey = "second-key";
        handler.EnqueueJson("""{"page":1,"results":[],"total_pages":1,"total_results":0}""");
        await service.SearchMoviesAsync("dune");

        await Assert.That(Param(handler.Requests[0].Uri, "api_key")).IsEqualTo("first-key");
        await Assert.That(Param(handler.Requests[1].Uri, "api_key")).IsEqualTo("second-key");
    }

    [Test]
    public async Task Search_ReturnsEmpty_WhenNotConfigured()
    {
        FakeHttpMessageHandler handler = new();
        TmdbService service = new("", "", null, handler);
        List<TmdbSearchResult> results = await service.SearchMoviesAsync("dune");
        await Assert.That(results.Count).IsEqualTo(0);
        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Search_UsesApiKeyQuery_WhenNoReadToken()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"page":1,"results":[],"total_pages":1,"total_results":0}""");
        TmdbService service = Service(handler, apiKey: ApiKey, readToken: null);

        await service.SearchMoviesAsync("dune");

        CapturedRequest request = handler.Requests.Single();
        await Assert.That(Param(request.Uri, "api_key")).IsEqualTo(ApiKey);
        await Assert.That(request.AuthParameter).IsNull();
    }

    [Test]
    public async Task Search_UsesBearerToken_WhenReadTokenProvided()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"page":1,"results":[],"total_pages":1,"total_results":0}""");
        TmdbService service = Service(handler, apiKey: null, readToken: Token);

        await service.SearchMoviesAsync("dune");

        CapturedRequest request = handler.Requests.Single();
        await Assert.That(request.AuthParameter).IsEqualTo(Token);
        await Assert.That(Param(request.Uri, "api_key")).IsNull();
    }

    // Search request shaping

    [Test]
    public async Task SearchMovies_UsesMovieEndpoint_AndEncodesQuery()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"page":1,"results":[],"total_pages":1,"total_results":0}""");
        TmdbService service = Service(handler);

        await service.SearchMoviesAsync("the matrix & co", page: 3, year: 1999);

        Uri uri = handler.Requests.Single().Uri;
        await Assert.That(uri.AbsolutePath).IsEqualTo("/3/search/movie");
        await Assert.That(Param(uri, "query")).IsEqualTo("the matrix & co");
        await Assert.That(Param(uri, "page")).IsEqualTo("3");
        await Assert.That(Param(uri, "year")).IsEqualTo("1999");
        await Assert.That(Param(uri, "include_adult")).IsEqualTo("false");
    }

    [Test]
    public async Task SearchTv_UsesFirstAirDateYear()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"page":1,"results":[],"total_pages":1,"total_results":0}""");
        TmdbService service = Service(handler);

        await service.SearchTvAsync("severance", year: 2022);

        Uri uri = handler.Requests.Single().Uri;
        await Assert.That(uri.AbsolutePath).IsEqualTo("/3/search/tv");
        await Assert.That(Param(uri, "first_air_date_year")).IsEqualTo("2022");
        await Assert.That(Param(uri, "year")).IsNull();
    }

    [Test]
    public async Task SearchMulti_UsesMultiEndpoint()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"page":1,"results":[],"total_pages":1,"total_results":0}""");
        TmdbService service = Service(handler);

        await service.SearchMultiAsync("blade runner");

        await Assert.That(handler.Requests.Single().Uri.AbsolutePath).IsEqualTo("/3/search/multi");
    }

    [Test]
    public async Task Search_ClampsPage()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"page":1,"results":[],"total_pages":1,"total_results":0}""");
        TmdbService service = Service(handler);

        await service.SearchMoviesAsync("x", page: 900);

        await Assert.That(Param(handler.Requests.Single().Uri, "page")).IsEqualTo("500");
    }

    // Search parsing

    [Test]
    public async Task SearchMovies_ParsesResults()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""
        {
          "page": 1,
          "total_pages": 5,
          "total_results": 100,
          "results": [
            {
              "id": 603,
              "media_type": "movie",
              "title": "The Matrix",
              "original_title": "The Matrix",
              "overview": "A hacker learns the truth.",
              "poster_path": "/poster.jpg",
              "backdrop_path": "/backdrop.jpg",
              "release_date": "1999-03-31",
              "vote_average": 8.2,
              "vote_count": 24000,
              "popularity": 55.3,
              "genre_ids": [28, 878],
              "original_language": "en",
              "adult": false
            }
          ]
        }
        """);
        TmdbService service = Service(handler);

        List<TmdbSearchResult> results = await service.SearchMoviesAsync("matrix");

        await Assert.That(results.Count).IsEqualTo(1);
        TmdbSearchResult movie = results[0];
        await Assert.That(movie.Id).IsEqualTo(603);
        await Assert.That(movie.DisplayTitle).IsEqualTo("The Matrix");
        await Assert.That(movie.Year).IsEqualTo(1999);
        await Assert.That(movie.VoteAverage).IsEqualTo(8.2);
        await Assert.That(movie.VoteCount).IsEqualTo(24000);
        await Assert.That(movie.GenreIds).Contains(878);
        await Assert.That(movie.IsTv).IsFalse();
    }

    [Test]
    public async Task SearchTv_ParsesNameAndFirstAirDate()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""
        {
          "results": [
            {
              "id": 1396,
              "name": "Breaking Bad",
              "original_name": "Breaking Bad",
              "first_air_date": "2008-01-20",
              "media_type": "tv",
              "origin_country": ["US"],
              "vote_average": 8.9
            }
          ]
        }
        """);
        TmdbService service = Service(handler);

        List<TmdbSearchResult> results = await service.SearchTvAsync("breaking bad");

        TmdbSearchResult show = results[0];
        await Assert.That(show.DisplayTitle).IsEqualTo("Breaking Bad");
        await Assert.That(show.Year).IsEqualTo(2008);
        await Assert.That(show.IsTv).IsTrue();
        await Assert.That(show.OriginCountry).Contains("US");
    }

    [Test]
    public async Task Search_ReturnsEmpty_OnServerError()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueStatus(HttpStatusCode.InternalServerError);
        TmdbService service = Service(handler);

        List<TmdbSearchResult> results = await service.SearchMoviesAsync("dune");

        await Assert.That(results.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Search_ReturnsEmpty_OnMalformedJson()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("this is not json");
        TmdbService service = Service(handler);

        List<TmdbSearchResult> results = await service.SearchMoviesAsync("dune");

        await Assert.That(results.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Search_ReturnsEmpty_OnTransportException()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueThrow(new HttpRequestException("boom"));
        TmdbService service = Service(handler);

        List<TmdbSearchResult> results = await service.SearchMoviesAsync("dune");

        await Assert.That(results.Count).IsEqualTo(0);
    }

    // Trending / curated lists

    [Test]
    public async Task Trending_BuildsPath_AndWindow()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"results":[]}""");
        TmdbService service = Service(handler);

        await service.GetTrendingAsync(TmdbMediaType.Movie, TmdbWindow.Day, page: 2);

        Uri uri = handler.Requests.Single().Uri;
        await Assert.That(uri.AbsolutePath).IsEqualTo("/3/trending/movie/day");
        await Assert.That(Param(uri, "page")).IsEqualTo("2");
    }

    [Test]
    public async Task TrendingAll_UsesAllSegment()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"results":[]}""");
        TmdbService service = Service(handler);

        await service.GetTrendingAsync(TmdbMediaType.All, TmdbWindow.Week);

        await Assert.That(handler.Requests.Single().Uri.AbsolutePath).IsEqualTo("/3/trending/all/week");
    }

    [Test]
    public async Task GetList_BuildsMovieAndTvPaths()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"results":[]}""");
        handler.EnqueueJson("""{"results":[]}""");
        TmdbService service = Service(handler);

        await service.GetListAsync(TmdbMediaType.Movie, "top_rated");
        await service.GetListAsync(TmdbMediaType.Tv, "on_the_air");

        await Assert.That(handler.Requests[0].Uri.AbsolutePath).IsEqualTo("/3/movie/top_rated");
        await Assert.That(handler.Requests[1].Uri.AbsolutePath).IsEqualTo("/3/tv/on_the_air");
    }

    // Discover

    [Test]
    public async Task Discover_BuildsAllFilters()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"results":[]}""");
        TmdbService service = Service(handler);

        await service.DiscoverAsync(new TmdbDiscoverQuery
        {
            MediaType = TmdbMediaType.Tv,
            Page = 2,
            SortBy = "first_air_date.desc",
            WithGenres = "16,10759",
            WithoutGenres = "10762",
            WithOriginCountry = "JP",
            FirstAirDateGte = "2024-01-01",
            FirstAirDateLte = "2024-03-31",
            VoteCountGte = 5,
            VoteAverageGte = 7.5
        });

        Uri uri = handler.Requests.Single().Uri;
        await Assert.That(uri.AbsolutePath).IsEqualTo("/3/discover/tv");
        await Assert.That(Param(uri, "with_genres")).IsEqualTo("16,10759");
        await Assert.That(Param(uri, "without_genres")).IsEqualTo("10762");
        await Assert.That(Param(uri, "with_origin_country")).IsEqualTo("JP");
        await Assert.That(Param(uri, "first_air_date.gte")).IsEqualTo("2024-01-01");
        await Assert.That(Param(uri, "first_air_date.lte")).IsEqualTo("2024-03-31");
        await Assert.That(Param(uri, "vote_count.gte")).IsEqualTo("5");
        await Assert.That(Param(uri, "vote_average.gte")).IsEqualTo("7.5");
        await Assert.That(Param(uri, "sort_by")).IsEqualTo("first_air_date.desc");
        await Assert.That(Param(uri, "page")).IsEqualTo("2");
    }

    [Test]
    public async Task Discover_SendsLanguageExactlyOnce()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"results":[]}""");
        TmdbService service = Service(handler);

        await service.DiscoverAsync(new TmdbDiscoverQuery { MediaType = TmdbMediaType.Tv, WithGenres = "16" });

        string query = handler.Requests.Single().Uri.Query;
        int languageCount = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Count(pair => pair.StartsWith("language=", StringComparison.OrdinalIgnoreCase));
        await Assert.That(languageCount).IsEqualTo(1);
    }

    [Test]
    public async Task Discover_Movie_UsesPrimaryReleaseFilters()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"results":[]}""");
        TmdbService service = Service(handler);

        await service.DiscoverAsync(new TmdbDiscoverQuery
        {
            MediaType = TmdbMediaType.Movie,
            Year = 2024,
            ReleaseDateGte = "2024-01-01"
        });

        Uri uri = handler.Requests.Single().Uri;
        await Assert.That(uri.AbsolutePath).IsEqualTo("/3/discover/movie");
        await Assert.That(Param(uri, "primary_release_year")).IsEqualTo("2024");
        await Assert.That(Param(uri, "primary_release_date.gte")).IsEqualTo("2024-01-01");
        await Assert.That(Param(uri, "first_air_date_year")).IsNull();
    }

    // Details

    [Test]
    public async Task GetMovieDetail_ParsesExternalIdsCreditsVideosAndCert()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""
        {
          "id": 603,
          "title": "The Matrix",
          "overview": "Neo.",
          "runtime": 136,
          "release_date": "1999-03-31",
          "vote_average": 8.2,
          "genres": [{ "id": 878, "name": "Science Fiction" }],
          "external_ids": { "imdb_id": "tt0133093" },
          "credits": { "cast": [ { "id": 6384, "name": "Keanu Reeves", "character": "Neo", "profile_path": "/keanu.jpg", "order": 0 } ] },
          "videos": { "results": [ { "key": "vKQi3bBA1y8", "site": "YouTube", "type": "Trailer", "official": true } ] },
          "release_dates": { "results": [ { "iso_3166_1": "GB", "release_dates": [ { "certification": "15", "type": 3 } ] } ] }
        }
        """);
        TmdbService service = Service(handler);

        TmdbMovieDetail? detail = await service.GetMovieDetailAsync(603);

        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.Title).IsEqualTo("The Matrix");
        await Assert.That(detail.Runtime).IsEqualTo(136);
        await Assert.That(detail.ExternalIds!.ImdbId).IsEqualTo("tt0133093");
        await Assert.That(detail.Credits!.Cast[0].Name).IsEqualTo("Keanu Reeves");
        await Assert.That(detail.Videos!.Results[0].Key).IsEqualTo("vKQi3bBA1y8");
        await Assert.That(detail.ReleaseDates!.Results[0].ReleaseDates[0].Certification).IsEqualTo("15");
        await Assert.That(Param(handler.Requests.Single().Uri, "append_to_response")).Contains("external_ids");
        // Must request English/neutral artwork explicitly or TMDB hides logos when a language is set.
        await Assert.That(Param(handler.Requests.Single().Uri, "include_image_language")).IsEqualTo("en,null");
    }

    [Test]
    public async Task GetTvDetail_ParsesTvdbIdSeasonsAndContentRatings()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""
        {
          "id": 1396,
          "name": "Breaking Bad",
          "first_air_date": "2008-01-20",
          "number_of_seasons": 5,
          "number_of_episodes": 62,
          "episode_run_time": [47],
          "origin_country": ["US"],
          "genres": [{ "id": 18, "name": "Drama" }],
          "networks": [{ "id": 174, "name": "AMC" }],
          "external_ids": { "tvdb_id": 81189, "imdb_id": "tt0903747" },
          "seasons": [
            { "season_number": 0, "episode_count": 12 },
            { "season_number": 1, "episode_count": 7 },
            { "season_number": 2, "episode_count": 13 }
          ],
          "content_ratings": { "results": [ { "iso_3166_1": "GB", "rating": "18" } ] }
        }
        """);
        TmdbService service = Service(handler);

        TmdbTvDetail? detail = await service.GetTvDetailAsync(1396);

        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.ExternalIds!.TvdbId).IsEqualTo(81189);
        await Assert.That(detail.NumberOfSeasons).IsEqualTo(5);
        await Assert.That(detail.Runtime).IsEqualTo(47);
        await Assert.That(detail.Seasons.Count).IsEqualTo(3);
        await Assert.That(detail.ContentRatings!.Results[0].Rating).IsEqualTo("18");
        await Assert.That(detail.Networks[0].Name).IsEqualTo("AMC");
    }

    [Test]
    public async Task MovieDetail_ParsesTagline()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{ "id": 603, "title": "The Matrix", "tagline": "Welcome to the Real World." }""");
        TmdbService service = Service(handler);

        TmdbMovieDetail? detail = await service.GetMovieDetailAsync(603);

        await Assert.That(detail!.Tagline).IsEqualTo("Welcome to the Real World.");
    }

    [Test]
    public async Task FindReleaseDate_PrefersRequestedCountryAndType()
    {
        TmdbReleaseDates dates = new()
        {
            Results =
            [
                new() { Country = "US", ReleaseDates = [new() { Type = TmdbReleaseType.Physical, ReleaseDate = "2024-05-01T00:00:00.000Z" }] },
                new() { Country = "GB", ReleaseDates = [new() { Type = TmdbReleaseType.Physical, ReleaseDate = "2024-04-22T00:00:00.000Z" }] },
                new() { Country = "GB", ReleaseDates = [new() { Type = TmdbReleaseType.Digital, ReleaseDate = "2024-03-01T00:00:00.000Z" }] }
            ]
        };

        await Assert.That(TmdbService.FindReleaseDate(dates, TmdbReleaseType.Physical, "GB", "US"))
            .IsEqualTo("2024-04-22T00:00:00.000Z");
        await Assert.That(TmdbService.FindReleaseDate(dates, TmdbReleaseType.Digital, "GB", "US"))
            .IsEqualTo("2024-03-01T00:00:00.000Z");
        await Assert.That(TmdbService.FindReleaseDate(dates, TmdbReleaseType.Tv, "GB")).IsNull();
        await Assert.That(TmdbService.FindReleaseDate(null, TmdbReleaseType.Physical)).IsNull();
    }

    [Test]
    public async Task RepeatedRequests_AreServedFromCache()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"id":603,"title":"The Matrix"}""");
        TmdbService service = Service(handler);

        TmdbMovieDetail? first = await service.GetMovieDetailAsync(603);
        TmdbMovieDetail? second = await service.GetMovieDetailAsync(603);

        await Assert.That(first!.Title).IsEqualTo("The Matrix");
        await Assert.That(second!.Title).IsEqualTo("The Matrix");
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Recommendations_BuildMovieAndTvPaths()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"results":[]}""");
        handler.EnqueueJson("""{"results":[]}""");
        TmdbService service = Service(handler);

        await service.GetMovieRecommendationsAsync(603);
        await service.GetTvRecommendationsAsync(1396);

        await Assert.That(handler.Requests[0].Uri.AbsolutePath).IsEqualTo("/3/movie/603/recommendations");
        await Assert.That(handler.Requests[1].Uri.AbsolutePath).IsEqualTo("/3/tv/1396/recommendations");
    }

    [Test]
    public async Task GetMovieDetail_ReturnsNull_OnNotFound()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueStatus(HttpStatusCode.NotFound);
        TmdbService service = Service(handler);

        TmdbMovieDetail? detail = await service.GetMovieDetailAsync(999999);

        await Assert.That(detail).IsNull();
    }

    // Genres

    [Test]
    public async Task GetGenres_ParsesList_AndUsesCorrectEndpoint()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"genres":[{"id":16,"name":"Animation"},{"id":35,"name":"Comedy"}]}""");
        TmdbService service = Service(handler);

        List<TmdbGenre> genres = await service.GetGenresAsync(TmdbMediaType.Tv);

        await Assert.That(handler.Requests.Single().Uri.AbsolutePath).IsEqualTo("/3/genre/tv/list");
        await Assert.That(genres.Count).IsEqualTo(2);
        await Assert.That(genres[0].Name).IsEqualTo("Animation");
    }

    // Seasons / episodes

    [Test]
    public async Task GetTvSeason_ParsesEpisodes()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""
        {
          "season_number": 1,
          "episodes": [
            { "episode_number": 1, "name": "Pilot", "overview": "Starts.", "still_path": "/still.jpg", "vote_average": 8.0, "runtime": 58 }
          ]
        }
        """);
        TmdbService service = Service(handler);

        TmdbTvSeason? season = await service.GetTvSeasonAsync(1396, 1);

        await Assert.That(season).IsNotNull();
        await Assert.That(handler.Requests.Single().Uri.AbsolutePath).IsEqualTo("/3/tv/1396/season/1");
        await Assert.That(season!.Episodes.Count).IsEqualTo(1);
        await Assert.That(season.Episodes[0].Name).IsEqualTo("Pilot");
        await Assert.That(season.Episodes[0].Runtime).IsEqualTo(58);
    }

    // Image helpers

    [Test]
    public async Task ImageHelpers_BuildCorrectUrls()
    {
        await Assert.That(TmdbService.GetPosterUrl("/p.jpg")).IsEqualTo("https://image.tmdb.org/t/p/w500/p.jpg");
        await Assert.That(TmdbService.GetPosterUrl("/p.jpg", "w200")).IsEqualTo("https://image.tmdb.org/t/p/w200/p.jpg");
        await Assert.That(TmdbService.GetBackdropUrl("/b.jpg")).IsEqualTo("https://image.tmdb.org/t/p/w780/b.jpg");
        await Assert.That(TmdbService.GetStillUrl("/s.jpg", "w185")).IsEqualTo("https://image.tmdb.org/t/p/w185/s.jpg");
        await Assert.That(TmdbService.GetProfileUrl("/c.jpg")).IsEqualTo("https://image.tmdb.org/t/p/w185/c.jpg");
    }

    [Test]
    public async Task FindYouTubeTrailer_PrefersOfficialTrailer()
    {
        List<TmdbVideo> videos =
        [
            new() { Key = "teaser", Site = "YouTube", Type = "Teaser" },
            new() { Key = "trailer", Site = "YouTube", Type = "Trailer", Official = true },
            new() { Key = "vimeo", Site = "Vimeo", Type = "Trailer" }
        ];

        await Assert.That(TmdbService.FindYouTubeTrailer(videos)).IsEqualTo("trailer");
        await Assert.That(TmdbService.FindYouTubeTrailer([])).IsNull();
        await Assert.That(TmdbService.FindYouTubeTrailer(null)).IsNull();
    }

    [Test]
    public async Task GetLogoUrl_BuildsUrl()
    {
        await Assert.That(TmdbService.GetLogoUrl("/logo.png")).IsEqualTo("https://image.tmdb.org/t/p/w92/logo.png");
        await Assert.That(TmdbService.GetLogoUrl(null)).IsEqualTo("");
    }

    [Test]
    public async Task FindBestLogo_PrefersEnglishThenHighestVoted()
    {
        List<TmdbImage> logos =
        [
            new() { FilePath = "/fr.png", Language = "fr", VoteAverage = 9 },
            new() { FilePath = "/en-low.png", Language = "en", VoteAverage = 1 },
            new() { FilePath = "/en-high.png", Language = "en", VoteAverage = 8 }
        ];

        await Assert.That(TmdbService.FindBestLogo(logos)).IsEqualTo("https://image.tmdb.org/t/p/w300/en-high.png");
        await Assert.That(TmdbService.FindBestLogo([])).IsEqualTo("");
        await Assert.That(TmdbService.FindBestLogo(null)).IsEqualTo("");
    }

    [Test]
    public async Task ImageHelpers_ReturnEmpty_ForNullPath()
    {
        await Assert.That(TmdbService.GetPosterUrl(null)).IsEqualTo("");
        await Assert.That(TmdbService.GetPosterUrl("")).IsEqualTo("");
        await Assert.That(TmdbService.GetBackdropUrl(null)).IsEqualTo("");
    }

    [Test]
    public async Task Requests_IncludeLanguageAndRegion()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"results":[]}""");
        TmdbService service = Service(handler);

        await service.SearchMoviesAsync("dune");

        Uri uri = handler.Requests.Single().Uri;
        await Assert.That(Param(uri, "language")).IsEqualTo("en-GB");
        await Assert.That(Param(uri, "region")).IsEqualTo("GB");
    }

    [Test]
    public async Task Search_PropagatesCancellation()
    {
        FakeHttpMessageHandler handler = new();
        handler.Enqueue(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return FakeHttpMessageHandler.JsonResponse("""{"results":[]}""");
        });
        TmdbService service = Service(handler);
        using CancellationTokenSource cts = new();
        cts.CancelAfter(50);

        await Assert.That(async () => await service.SearchMoviesAsync("dune", ct: cts.Token))
            .Throws<OperationCanceledException>();
    }
}
