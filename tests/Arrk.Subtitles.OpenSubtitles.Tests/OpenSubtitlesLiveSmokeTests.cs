using Arrk.Subtitles.OpenSubtitles;
using Arrk.TestSupport;

namespace Arrk.Subtitles.OpenSubtitles.Tests;

/// <summary>
/// Optional live smoke test. It never runs in hermetic CI: set ARRK_OPENSUBTITLES_API_KEY to
/// a real OpenSubtitles consumer key. Downloads are not exercised so the daily quota is untouched.
/// </summary>
public class OpenSubtitlesLiveSmokeTests
{
    [Test]
    public async Task Search_KnownMovie_ReturnsCandidates()
    {
        string? apiKey = Environment.GetEnvironmentVariable("ARRK_OPENSUBTITLES_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)
            || Environment.GetEnvironmentVariable("ARRK_SKIP_LIVE_TESTS") == "1")
        {
            return;
        }

        OpenSubtitlesConfig config = new() { ApiKey = apiKey };
        OpenSubtitlesClient client = new(new MutableOptionsMonitor<OpenSubtitlesConfig>(config));

        IReadOnlyList<OpenSubtitlesSubtitle> results = await client.SearchAsync(new OpenSubtitlesSearchQuery
        {
            ImdbId = "0133093",
            Query = "The Matrix",
            Year = 1999,
            Type = "movie",
            Languages = ["en"]
        });

        await Assert.That(results).IsNotEmpty();
        await Assert.That(results.Any(result => result.Attributes.Files.Count > 0)).IsTrue();
    }
}
