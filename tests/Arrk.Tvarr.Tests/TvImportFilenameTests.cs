using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Tests;

public class TvImportFilenameTests
{
    [Test]
    public async Task ParsesMovieWithSceneDotsAndYear()
    {
        TvParsedFile parsed = TvImportFilename.Parse("Verity.2026.2160p.SDR.ITA-ENG.MULTI.WEBRip.x265.AAC-V3SP4EV3R.mkv");

        await Assert.That(parsed.CleanTitle).IsEqualTo("Verity");
        await Assert.That(parsed.Year).IsEqualTo(2026);
        await Assert.That(parsed.SeasonNumber).IsNull();
        await Assert.That(parsed.EpisodeNumber).IsNull();
    }

    [Test]
    public async Task ParsesMovieWithNumericTitleAndReleaseYear()
    {
        TvParsedFile parsed = TvImportFilename.Parse("1917.2019.1080p.BluRay.x264-GROUP.mkv");

        await Assert.That(parsed.CleanTitle).IsEqualTo("1917");
        await Assert.That(parsed.Year).IsEqualTo(2019);
    }

    [Test]
    public async Task ParsesEpisodeWithSeasonAndEpisode()
    {
        TvParsedFile parsed = TvImportFilename.Parse(
            "Re.Zero.Starting.Life.in.Another.World.S01E01.1080p.BluRay.Opus2.0.x264-Flugel.mkv");

        await Assert.That(parsed.CleanTitle).IsEqualTo("Re Zero Starting Life in Another World");
        await Assert.That(parsed.SeasonNumber).IsEqualTo(1);
        await Assert.That(parsed.EpisodeNumber).IsEqualTo(1);
        await Assert.That(parsed.EpisodeLabel).IsEqualTo("S01E01");
    }

    [Test]
    public async Task ParsesCrossEpisodeForm()
    {
        TvParsedFile parsed = TvImportFilename.Parse("Some.Show.2x04.HDTV.mkv");

        await Assert.That(parsed.CleanTitle).IsEqualTo("Some Show");
        await Assert.That(parsed.SeasonNumber).IsEqualTo(2);
        await Assert.That(parsed.EpisodeNumber).IsEqualTo(4);
    }

    [Test]
    public async Task ParsesMultiEpisodeRange()
    {
        TvParsedFile parsed = TvImportFilename.Parse("Some.Show.S03E05-E07.1080p.WEB.mkv");

        await Assert.That(parsed.SeasonNumber).IsEqualTo(3);
        await Assert.That(parsed.EpisodeNumber).IsEqualTo(5);
        await Assert.That(parsed.EndEpisodeNumber).IsEqualTo(7);
        await Assert.That(parsed.IsMultiEpisode).IsTrue();
    }

    [Test]
    public async Task ParsesSeasonPack()
    {
        TvParsedFile parsed = TvImportFilename.Parse("Some.Show.Season 2 Complete 1080p WEB-DL.mkv");

        await Assert.That(parsed.CleanTitle).IsEqualTo("Some Show");
        await Assert.That(parsed.SeasonNumber).IsEqualTo(2);
        await Assert.That(parsed.EpisodeNumber).IsNull();
        await Assert.That(parsed.IsSeasonPack).IsTrue();
    }

    [Test]
    public async Task ParsesAnimeAbsoluteNumber()
    {
        TvParsedFile parsed = TvImportFilename.Parse("[SubsPlease] One Piece - 1085 (1080p).mkv");

        await Assert.That(parsed.CleanTitle).IsEqualTo("One Piece");
        await Assert.That(parsed.EpisodeNumber).IsEqualTo(1085);
        await Assert.That(parsed.SeasonNumber).IsNull();
    }

    [Test]
    public async Task UsesTheLastYearAndDropsTheEditionTail()
    {
        TvParsedFile parsed = TvImportFilename.Parse("Blade.Runner.2049.2017.Directors.Cut.1080p.BluRay.x264-GROUP.mkv");

        await Assert.That(parsed.CleanTitle).IsEqualTo("Blade Runner 2049");
        await Assert.That(parsed.Year).IsEqualTo(2017);
    }

    [Test]
    public async Task UnknownExtensionAndUnusualNameStillProduceATitle()
    {
        TvParsedFile parsed = TvImportFilename.Parse("Some Movie.mkv");

        await Assert.That(parsed.CleanTitle).IsEqualTo("Some Movie");
        await Assert.That(parsed.Year).IsNull();
    }

    [Test]
    public async Task NormalizeIsAccentAndPunctuationInsensitive()
    {
        await Assert.That(TvImportFilename.Normalize("Amélie & Friends!")).IsEqualTo("amelie and friends");
        await Assert.That(TvImportFilename.Normalize("Spider-Man: No Way Home")).IsEqualTo("spider man no way home");
    }
}
