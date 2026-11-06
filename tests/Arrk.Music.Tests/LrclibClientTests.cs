using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Music.Lyrics;
using Arrk.TestSupport;

namespace Arrk.Music.Tests;

/// <summary>
/// Covers the LRCLIB client's contract: a miss and a provider outage must be distinguishable, or the
/// lyrics job would record "we can never find this" for a temporary hiccup.
/// </summary>
public class LrclibClientTests
{
    static LrclibClient Client(FakeHttpMessageHandler handler) =>
        new(NullLogger<LrclibClient>.Instance, handler);

    const string SyncedRecord = """
        {
          "id": 1,
          "trackName": "Hysteria",
          "artistName": "Muse",
          "albumName": "Absolution",
          "duration": 213.0,
          "instrumental": false,
          "plainLyrics": "It's bugging me",
          "syncedLyrics": "[00:41.16] It's bugging me",
          "hasWordSync": false,
          "lyricsfile": ""
        }
        """;

    [Test]
    public async Task Get_MapsTheRecord()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler().EnqueueJson(SyncedRecord);

        LyricsCandidate? candidate = await Client(handler).GetAsync("Muse", "Hysteria", "Absolution", 213);

        await Assert.That(candidate).IsNotNull();
        await Assert.That(candidate!.TrackName).IsEqualTo("Hysteria");
        await Assert.That(candidate.DurationSeconds).IsEqualTo(213);
        await Assert.That(candidate.SyncedLyrics).Contains("bugging");
        await Assert.That(candidate.HasLyrics).IsTrue();
    }

    [Test]
    public async Task Get_ReturnsNullWhenTheTrackIsNotInTheDatabase()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.NotFound, json: """{"message":"not found"}""");

        LyricsCandidate? candidate = await Client(handler).GetAsync("Nobody", "Nothing", null, 1);

        await Assert.That(candidate).IsNull();
    }

    [Test]
    public async Task Get_RetriesATransientOverloadAndSucceeds()
    {
        // The real-world case: LRCLIB answers "The server is busy, please retry in a moment" with a
        // 503, and the very same request succeeds a moment later. One attempt would lose the song.
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.ServiceUnavailable, json: """{"message":"The server is busy, please retry in a moment"}""")
            .EnqueueJson(SyncedRecord);

        LyricsCandidate? candidate = await Client(handler).GetAsync("Muse", "Hysteria", null, 213);

        await Assert.That(candidate).IsNotNull();
        await Assert.That(handler.Requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Get_RetriesRateLimiting()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.TooManyRequests)
            .EnqueueJson(SyncedRecord);

        await Assert.That(await Client(handler).GetAsync("Muse", "Hysteria", null, 213)).IsNotNull();
    }

    [Test]
    public async Task Get_DoesNotRetryAGenuineMiss()
    {
        // A 404 is an answer, not a failure: retrying it would triple the load for nothing.
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.NotFound, json: """{"message":"not found"}""");

        await Assert.That(await Client(handler).GetAsync("Nobody", "Nothing", null, 1)).IsNull();
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Get_GivesUpAfterTheAttemptLimitAndReportsTheProviderAsUnavailable()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler { Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("""{"message":"busy"}""", HttpStatusCode.ServiceUnavailable)) };

        await Assert.ThrowsAsync<LyricsProviderUnavailableException>(() => Client(handler).GetAsync("Muse", "Hysteria", null, 213));
        await Assert.That(handler.Requests.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Get_ThrowsWhenTheProviderIsStillOverloadedAfterRetrying()
    {
        // LRCLIB returns 503 with a JSON body under load; after the retries are exhausted that must
        // NOT read as "no lyrics", it must surface as unavailable so the song is retried later.
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler { Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("""{"message":"The server is busy","name":"ServerOverloaded","statusCode":503}""", HttpStatusCode.ServiceUnavailable)) };

        await Assert.ThrowsAsync<LyricsProviderUnavailableException>(() => Client(handler).GetAsync("Muse", "Hysteria", null, 213));
    }

    [Test]
    public async Task Search_ThrowsWhenRateLimited()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler { Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("{}", HttpStatusCode.TooManyRequests)) };
        await Assert.ThrowsAsync<LyricsProviderUnavailableException>(() => Client(handler).SearchAsync("Hysteria", "Muse"));
    }

    [Test]
    public async Task Search_ReturnsEmptyWhenNothingMatches()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler().EnqueueJson("[]");
        IReadOnlyList<LyricsCandidate> results = await Client(handler).SearchAsync("Nothing At All", "Nobody");
        await Assert.That(results.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Search_TreatsAnUnexpectedPayloadAsAProviderFailure()
    {
        // A 200 carrying an error object (not an array) is not something we can trust as "no
        // results", so it must surface as a retryable failure rather than a final miss.
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler().EnqueueJson("""{"message":"busy"}""");
        await Assert.ThrowsAsync<LyricsProviderUnavailableException>(() => Client(handler).SearchAsync("Hysteria", "Muse"));
    }

    [Test]
    public async Task Search_SendsTheStructuredQueryParts()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler().EnqueueJson("[]");

        await Client(handler).SearchAsync("Rocket Man", "Elton John");

        string query = handler.Requests[^1].Uri.Query;
        await Assert.That(query).Contains("track_name=Rocket%20Man");
        await Assert.That(query).Contains("artist_name=Elton%20John");
    }

    [Test]
    public async Task Get_NoQueryIsSentWithoutAnArtistOrTitle()
    {
        FakeHttpMessageHandler handler = new FakeHttpMessageHandler();
        LyricsCandidate? candidate = await Client(handler).GetAsync("", "Hysteria", null, 0);
        await Assert.That(candidate).IsNull();
        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }
}
