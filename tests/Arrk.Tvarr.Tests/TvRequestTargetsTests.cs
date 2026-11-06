using Arrk.Requests;
using Arrk.Tvarr.Components.Shared;
using Arrk.Ui.Components;
using Arrk.Tvarr.Services;
using Arrk.Core.Enums;
using Arrk.Core.Requests;

namespace Arrk.Tvarr.Tests;

public class TvRequestTargetsTests
{
    static MediaItem Movie() => new()
    {
        Title = "Dune",
        Year = 2021,
        MediaType = "movie",
        TmdbId = 438631,
        ImdbId = "tt1160419",
        PosterUrl = "/dune.jpg"
    };

    static MediaItem Show() => new()
    {
        Title = "Severance",
        Year = 2022,
        MediaType = "tv",
        TmdbId = 95396,
        TvdbId = 371980,
        PosterUrl = "/sev.jpg",
        SeasonCount = 2,
        AvailableSeasonNumbers = [1, 2]
    };

    [Test]
    public async Task Movie_target_carries_tmdb_and_imdb()
    {
        RequestTarget target = TvRequestTargets.ForMovie(Movie());

        await Assert.That(target.Domain).IsEqualTo(TvMediaDomains.Movie);
        await Assert.That(target.Kind).IsEqualTo(RequestKind.Movie);
        await Assert.That(target.ProviderIds["tmdb"]).IsEqualTo("438631");
        await Assert.That(target.ProviderIds["imdb"]).IsEqualTo("tt1160419");
        await Assert.That(target.PosterUrl).IsEqualTo("/dune.jpg");
        await Assert.That(target.ProviderIds.ContainsKey("tvdb")).IsFalse();
    }

    [Test]
    public async Task Series_target_carries_tvdb_and_tmdb()
    {
        RequestTarget target = TvRequestTargets.ForSeries(Show());

        await Assert.That(target.Domain).IsEqualTo(TvMediaDomains.Tv);
        await Assert.That(target.Kind).IsEqualTo(RequestKind.Series);
        await Assert.That(target.ProviderIds["tvdb"]).IsEqualTo("371980");
        await Assert.That(target.ProviderIds["tmdb"]).IsEqualTo("95396");
    }

    [Test]
    public async Task Season_and_episode_targets_carry_scope()
    {
        RequestTarget season = TvRequestTargets.ForSeason(Show(), 2);
        RequestTarget episode = TvRequestTargets.ForEpisode(Show(), 2, 5);

        await Assert.That(season.Kind).IsEqualTo(RequestKind.Season);
        await Assert.That(season.SeasonNumber).IsEqualTo(2);
        await Assert.That(season.EpisodeNumber).IsNull();

        await Assert.That(episode.Kind).IsEqualTo(RequestKind.Episode);
        await Assert.That(episode.SeasonNumber).IsEqualTo(2);
        await Assert.That(episode.EpisodeNumber).IsEqualTo(5);
    }

}
