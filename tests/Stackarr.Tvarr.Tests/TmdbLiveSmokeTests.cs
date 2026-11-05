using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr.Tests;

/// <summary>
/// Optional live smoke tests against the real TMDB API. They only run when
/// <c>TMDB_TOKEN</c> (or <c>TMDB_API_KEY</c>) is set, so normal CI stays hermetic.
/// These verify request shaping end-to-end and that our DTOs match TMDB's real payloads.
/// </summary>
public class TmdbLiveSmokeTests
{
    static TmdbService? LiveService()
    {
        string? token = Environment.GetEnvironmentVariable("TMDB_TOKEN");
        string? key = Environment.GetEnvironmentVariable("TMDB_API_KEY");
        if (string.IsNullOrEmpty(token) && string.IsNullOrEmpty(key))
            return null;
        return new TmdbService(key ?? "", token ?? "");
    }

    [Test]
    public async Task SearchMovies_Live_ReturnsMatrix()
    {
        TmdbService? tmdb = LiveService();
        if (tmdb is null) return;

        List<TmdbSearchResult> results = await tmdb.SearchMoviesAsync("The Matrix");

        await Assert.That(results.Count).IsGreaterThan(0);
        await Assert.That(results.Any(r => r.DisplayTitle.Contains("Matrix", StringComparison.OrdinalIgnoreCase))).IsTrue();
        await Assert.That(results[0].PosterPath).IsNotNull();
    }

    [Test]
    public async Task SearchTv_Live_ReturnsBreakingBad()
    {
        TmdbService? tmdb = LiveService();
        if (tmdb is null) return;

        List<TmdbSearchResult> results = await tmdb.SearchTvAsync("Breaking Bad");

        await Assert.That(results.Count).IsGreaterThan(0);
        await Assert.That(results.Any(r => r.DisplayTitle.Contains("Breaking Bad", StringComparison.OrdinalIgnoreCase))).IsTrue();
        await Assert.That(results[0].IsTv).IsTrue();
    }

    [Test]
    public async Task Trending_Live_ReturnsResults()
    {
        TmdbService? tmdb = LiveService();
        if (tmdb is null) return;

        List<TmdbSearchResult> movies = await tmdb.GetTrendingAsync(TmdbMediaType.Movie, TmdbWindow.Week);

        await Assert.That(movies.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task DiscoverAnime_Live_ReturnsJapaneseAnimation()
    {
        TmdbService? tmdb = LiveService();
        if (tmdb is null) return;

        List<TmdbSearchResult> results = await tmdb.DiscoverAsync(new TmdbDiscoverQuery
        {
            MediaType = TmdbMediaType.Tv,
            SortBy = "popularity.desc",
            WithGenres = "16",
            WithoutGenres = "10762",
            WithOriginCountry = "JP",
            VoteCountGte = 10
        });

        await Assert.That(results.Count).IsGreaterThan(0);
        await Assert.That(results.Any(r => r.GenreIds.Contains(16))).IsTrue();
    }

    [Test]
    public async Task MovieDetail_Live_ParsesExternalIdsAndCredits()
    {
        TmdbService? tmdb = LiveService();
        if (tmdb is null) return;

        TmdbMovieDetail? detail = await tmdb.GetMovieDetailAsync(603);

        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.Title).IsEqualTo("The Matrix");
        await Assert.That(detail.ExternalIds!.ImdbId).IsEqualTo("tt0133093");
        await Assert.That(detail.Credits!.Cast.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task MovieDetail_Live_ReturnsLogoArtwork()
    {
        TmdbService? tmdb = LiveService();
        if (tmdb is null) return;

        TmdbMovieDetail? detail = await tmdb.GetMovieDetailAsync(693134);

        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.Images).IsNotNull();
        await Assert.That(detail.Images!.Logos.Count).IsGreaterThan(0);
        await Assert.That(TmdbService.FindBestLogo(detail.Images.Logos)).IsNotEmpty();
    }

    [Test]
    public async Task TvDetail_Live_ResolvesTvdbId()
    {
        TmdbService? tmdb = LiveService();
        if (tmdb is null) return;

        TmdbTvDetail? detail = await tmdb.GetTvDetailAsync(1396);

        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.ExternalIds!.TvdbId).IsEqualTo(81189);
        await Assert.That(detail.Seasons.Count).IsGreaterThan(0);
    }
}
