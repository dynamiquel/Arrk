
using Arrk.Ui.Components;

namespace Arrk.Ui.Tests;

public class FormatHelperTests
{
    [Test]
    public async Task LanguageName_ConvertsIsoCodesToReadableNames()
    {
        await Assert.That(FormatHelper.LanguageName("en")).IsEqualTo("English");
        await Assert.That(FormatHelper.LanguageName("ja")).IsEqualTo("Japanese");
        await Assert.That(FormatHelper.LanguageName("zh")).IsEqualTo("Chinese");
        await Assert.That(FormatHelper.LanguageName("ko")).IsEqualTo("Korean");
        await Assert.That(FormatHelper.LanguageName("fr")).IsEqualTo("French");
    }

    [Test]
    public async Task LanguageName_PassesThroughNames_AndHandlesEmpty()
    {
        await Assert.That(FormatHelper.LanguageName("English")).IsEqualTo("English");
        await Assert.That(FormatHelper.LanguageName("")).IsEqualTo("Unknown");
        await Assert.That(FormatHelper.LanguageName(null)).IsEqualTo("Unknown");
    }

    [Test]
    public async Task FormatReleaseDate_ParsesIsoAndIsoTimestamp()
    {
        await Assert.That(FormatHelper.FormatReleaseDate("2024-03-01")).IsEqualTo("Mar 01, 2024");
        await Assert.That(FormatHelper.FormatReleaseDate("2024-03-01T00:00:00.000Z")).IsEqualTo("Mar 01, 2024");
        await Assert.That(FormatHelper.FormatReleaseDate("")).IsEqualTo("");
        await Assert.That(FormatHelper.FormatReleaseDate(null)).IsEqualTo("");
    }

    [Test]
    public async Task IsReleased_OnlyTrueForDatesOnOrBeforeToday()
    {
        string yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        string tomorrow = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        await Assert.That(FormatHelper.IsReleased(yesterday)).IsTrue();
        await Assert.That(FormatHelper.IsReleased(tomorrow)).IsFalse();
        await Assert.That(FormatHelper.IsReleased((string?)null)).IsFalse();
        await Assert.That(FormatHelper.IsReleased((DateTime?)null)).IsFalse();
    }

    [Test]
    public async Task MovieReleaseStage_ReturnsMostAdvancedWindowReached()
    {
        DateTime past = DateTime.UtcNow.AddDays(-10);
        DateTime future = DateTime.UtcNow.AddDays(10);

        // Physical is the latest window, so it wins when it has passed.
        await Assert.That(FormatHelper.MovieReleaseStage(past, past, past)).IsEqualTo("Physical Release");
        await Assert.That(FormatHelper.MovieReleaseStage(past, past, future)).IsEqualTo("Web Release");
        await Assert.That(FormatHelper.MovieReleaseStage(past, future, future)).IsEqualTo("In Cinema");
    }

    [Test]
    public async Task MovieReleaseStage_ReturnsNullWhenNothingReleasedYet()
    {
        DateTime future = DateTime.UtcNow.AddDays(10);
        await Assert.That(FormatHelper.MovieReleaseStage(future, future, future)).IsNull();
        await Assert.That(FormatHelper.MovieReleaseStage(null, null, null)).IsNull();
    }
}
