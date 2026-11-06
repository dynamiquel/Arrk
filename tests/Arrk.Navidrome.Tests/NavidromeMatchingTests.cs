using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Arrk.Navidrome;
using Arrk.TestSupport;

namespace Arrk.Navidrome.Tests;

/// <summary>Covers artist-aware song/album matching (a loose title search must not pick the wrong song).</summary>
public class NavidromeMatchingTests
{
    static (NavidromeService Service, FakeHttpMessageHandler Handler) Build()
    {
        FakeHttpMessageHandler handler = new();
        NavidromeClient client = new("http://navidrome:4533/navidrome/", "arrk", "secret", handler);
        NavidromeService service = new(
            client,
            new NavidromeApiSecret { Username = "arrk", Password = "secret" },
            new StaticOptionsMonitor<NavidromeConfig>(new NavidromeConfig { Enabled = true }),
            NullLogger<NavidromeService>.Instance);
        return (service, handler);
    }

    const string SearchResult = """
        { "subsonic-response": { "status": "ok", "version": "1.16.1",
            "searchResult3": { "song": [
                { "id": "s1", "title": "So Easy (To Fall in Love)", "artist": "Olivia Dean", "album": "The Art of Loving" },
                { "id": "s2", "title": "Easy", "artist": "Commodores", "album": "Commodores" } ] } } }
        """;

    [Test]
    public async Task FindSongId_PrefersTheArtistMatch()
    {
        (NavidromeService service, FakeHttpMessageHandler handler) = Build();
        handler.EnqueueJson(SearchResult);

        string? id = await service.FindSongIdAsync("Easy", "Commodores");

        await Assert.That(id).IsEqualTo("s2");
    }

    [Test]
    public async Task FindSongId_IgnoresAWrongArtistMatch_WhenTheTitleIsNotExact()
    {
        (NavidromeService service, FakeHttpMessageHandler handler) = Build();
        handler.EnqueueJson(SearchResult);

        // "Easy" by Commodores must NOT resolve to "So Easy" by Olivia Dean.
        string? id = await service.FindSongIdAsync("Easy", "Commodores");

        await Assert.That(id).IsNotEqualTo("s1");
    }

    [Test]
    public async Task FindSongId_ReturnsNull_WhenOnlyAWrongArtistLooseMatchExists()
    {
        (NavidromeService service, FakeHttpMessageHandler handler) = Build();
        handler.EnqueueJson("""
            { "subsonic-response": { "status": "ok",
                "searchResult3": { "song": [
                    { "id": "s1", "title": "So Easy (To Fall in Love)", "artist": "Olivia Dean" } ] } } }
            """);

        string? id = await service.FindSongIdAsync("Easy", "Commodores");

        await Assert.That(id).IsNull();
    }

    [Test]
    public async Task FindSongId_DoesNotMatch_WhenTheArtistTagIsWrong()
    {
        (NavidromeService service, FakeHttpMessageHandler handler) = Build();
        handler.EnqueueJson("""
            { "subsonic-response": { "status": "ok",
                "searchResult3": { "song": [
                    { "id": "s9", "title": "Dancing Queen", "artist": "Arrival (1976)" } ] } } }
            """);

        // A garbage artist tag must not match, the file needs re-tagging first, not a wrong link.
        string? id = await service.FindSongIdAsync("Dancing Queen", "ABBA");

        await Assert.That(id).IsNull();
    }

    [Test]
    public async Task FindSongId_Matches_WhenTheArtistTagIsEmpty()
    {
        (NavidromeService service, FakeHttpMessageHandler handler) = Build();
        handler.EnqueueJson("""
            { "subsonic-response": { "status": "ok",
                "searchResult3": { "song": [
                    { "id": "s9", "title": "Dancing Queen", "artist": "" } ] } } }
            """);

        string? id = await service.FindSongIdAsync("Dancing Queen", "ABBA");

        await Assert.That(id).IsEqualTo("s9");
    }

    sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
