using Stackarr.Tv.Abstractions;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr.Tests;

public class TvImportMatcherTests
{
    static TvParsedFile Movie(string name) => TvImportFilename.Parse(name);

    [Test]
    public async Task ExactTitleAndYearWins()
    {
        List<MovieLookupDto> results =
        [
            new() { TmdbId = 1, Title = "Verity", Year = 2026 },
            new() { TmdbId = 2, Title = "Verity", Year = 1998 }
        ];

        IReadOnlyList<TvImportCandidate> ranked = TvImportMatcher.RankMovies(
            Movie("Verity.2026.1080p.mkv"),
            results,
            new Dictionary<int, TvMovie>());

        await Assert.That(ranked[0].TmdbId).IsEqualTo(1);
        await Assert.That(ranked[0].Score).IsGreaterThan(ranked[1].Score);
        await Assert.That(ranked[0].Reasons.Any(reason => reason.Contains("Year 2026 matches"))).IsTrue();
    }

    [Test]
    public async Task RemakeWithTheWrongYearIsPenalised()
    {
        List<MovieLookupDto> results =
        [
            new() { TmdbId = 1, Title = "Dune", Year = 1984 },
            new() { TmdbId = 2, Title = "Dune", Year = 2021 }
        ];

        IReadOnlyList<TvImportCandidate> ranked = TvImportMatcher.RankMovies(
            Movie("Dune.2021.2160p.mkv"),
            results,
            new Dictionary<int, TvMovie>());

        await Assert.That(ranked[0].TmdbId).IsEqualTo(2);
        await Assert.That(ranked.Single(candidate => candidate.TmdbId == 1).Score).IsLessThan(ranked[0].Score);
    }

    [Test]
    public async Task LibraryMembershipBreaksTies()
    {
        List<MovieLookupDto> results =
        [
            new() { TmdbId = 1, Title = "Moana", Year = 2026 },
            new() { TmdbId = 2, Title = "Moana", Year = 2016 }
        ];

        Dictionary<int, TvMovie> library = new()
        {
            [2] = new TvMovie { Id = 8, Title = "Moana", Year = 2016, TmdbId = 2 }
        };

        IReadOnlyList<TvImportCandidate> ranked = TvImportMatcher.RankMovies(
            Movie("Moana.2016.1080p.mkv"),
            results,
            library);

        await Assert.That(ranked[0].TmdbId).IsEqualTo(2);
        await Assert.That(ranked[0].InLibrary).IsTrue();
        await Assert.That(ranked[0].LibraryId).IsEqualTo(8);
    }

    [Test]
    public async Task AnnualSeriesWithSeasonMarkerIsNotConfusedWithTheYear()
    {
        List<SeriesLookupDto> results =
        [
            new()
            {
                TvdbId = 10,
                TmdbId = 20,
                Title = "Re: ZERO, Starting Life in Another World",
                Year = 2016,
                Seasons = [new SeasonDto { SeasonNumber = 1 }]
            }
        ];

        IReadOnlyList<TvImportCandidate> ranked = TvImportMatcher.RankSeries(
            Movie("Re.Zero.Starting.Life.in.Another.World.S01E01.1080p.mkv"),
            results,
            new Dictionary<int, TvSeries>(),
            new Dictionary<int, TvSeries>());

        await Assert.That(ranked[0].TvdbId).IsEqualTo(10);
        await Assert.That(ranked[0].MediaType).IsEqualTo("tv");
        await Assert.That(ranked[0].Score).IsGreaterThan(TvImportMatcher.AutoPickThreshold);
    }

    [Test]
    public async Task SimilarityHandlesTypos()
    {
        await Assert.That(TvImportMatcher.Similarity("verity", "verity")).IsEqualTo(1.0);
        await Assert.That(TvImportMatcher.Similarity("moana", "moanna")).IsGreaterThan(0.8);
    }

    [Test]
    public async Task AmbiguityNeedsTwoCloseCandidates()
    {
        List<TvImportCandidate> clear =
        [
            new("movie", "A", "", 2020, 1, 0, "", "", 95, [], null, false, [], true),
            new("movie", "B", "", 2020, 2, 0, "", "", 40, [], null, false, [], true)
        ];

        List<TvImportCandidate> tied =
        [
            new("movie", "A", "", 2020, 1, 0, "", "", 80, [], null, false, [], true),
            new("movie", "B", "", 2020, 2, 0, "", "", 78, [], null, false, [], true)
        ];

        await Assert.That(TvImportMatcher.IsAmbiguous(clear)).IsFalse();
        await Assert.That(TvImportMatcher.IsAmbiguous(tied)).IsTrue();
    }
}
