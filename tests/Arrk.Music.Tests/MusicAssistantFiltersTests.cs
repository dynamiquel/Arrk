using Arrk.Music.Services;

namespace Arrk.Music.Tests;

/// <summary>
/// The assistant's music matching rules. Multi-word text is an AND search over title/artist/album
/// (so a plot-like keyword query is possible), genres match in either direction, and normalization
/// survives punctuation so the same album is not listed twice.
/// </summary>
public class MusicAssistantFiltersTests
{
    [Test]
    public async Task TextRequiresEveryToken()
    {
        await Assert.That(MusicAssistantFilters.MatchesText("never forget", "Never Forget", "Take That"))
            .IsTrue();
        await Assert.That(MusicAssistantFilters.MatchesText("never blue", "Never Forget", "Take That"))
            .IsFalse();
    }

    [Test]
    public async Task TextIsCaseInsensitive_AndSearchesTheAlbumField()
    {
        await Assert.That(MusicAssistantFilters.MatchesText("MASSIVE", "Unfinished Sympathy", "Massive Attack", "Blue Lines"))
            .IsTrue();
    }

    [Test]
    public async Task SingleTokenIgnoresPunctuation()
    {
        await Assert.That(MusicAssistantFilters.MatchesText("rezero", "Re:Zero", "Kenichiro Suehiro"))
            .IsTrue();
        await Assert.That(MusicAssistantFilters.MatchesText("re zero", "Re:Zero", "Kenichiro Suehiro"))
            .IsTrue();
    }

    [Test]
    public async Task GenreMatchesEitherDirection()
    {
        await Assert.That(MusicAssistantFilters.MatchesGenre(["Science Fiction", "Electronic"], "sci"))
            .IsTrue();
        await Assert.That(MusicAssistantFilters.MatchesGenre(["Jazz"], "smooth jazz"))
            .IsTrue();
        await Assert.That(MusicAssistantFilters.MatchesGenre(["Jazz"], "metal"))
            .IsFalse();
    }

    [Test]
    public async Task EmptyTextOrGenreMatchesEverything()
    {
        await Assert.That(MusicAssistantFilters.MatchesText("", "anything")).IsTrue();
        await Assert.That(MusicAssistantFilters.MatchesGenre(["anything"], null)).IsTrue();
    }

    [Test]
    public async Task NormalizeDropsPunctuationAndCase()
    {
        await Assert.That(MusicAssistantFilters.Normalize("Never Forget (Remastered)")).IsEqualTo("neverforgetremastered");
        await Assert.That(MusicAssistantFilters.Normalize(null)).IsEqualTo("");
    }
}
