using Microsoft.Extensions.Logging.Abstractions;
using Arrk.TestSupport;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Models;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Tests;

/// <summary>
/// Auto-resolve of a removed torrent against an arr: trace it back to its movie/episode, blocklist
/// the failed release and trigger a fresh search. Prefers the live queue; falls back to history,
/// which survives the download being removed from the client.
/// </summary>
public class FailedDownloadResolutionTests
{
    static RadarrService Radarr(FakeHttpMessageHandler handler) =>
        new(new RadarrClient("http://localhost", "key", handler), NullLogger<RadarrService>.Instance);

    static SonarrService Sonarr(FakeHttpMessageHandler handler) =>
        new(new SonarrClient("http://localhost", "key", handler), NullLogger<SonarrService>.Instance);

    [Test]
    public async Task Sonarr_HistoryLookup_UsesUppercaseInfohash()
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (request, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
                (request.Method, request.RequestUri!.AbsolutePath) switch
                {
                    (_, "/api/v3/queue") => """{ "records": [] }""",
                    (_, "/api/v3/history") =>
                        """{ "records": [ { "id": 21, "eventType": "grabbed", "downloadId": "AAAABBBB", "seriesId": 9, "episodeId": 634 } ] }""",
                    _ => "{}"
                }))
        };

        FailedDownloadResolution result = await Sonarr(handler).ResolveFailedDownload("aaaabbbb");

        await Assert.That(result.Found).IsTrue();
        CapturedRequest history = handler.Requests.First(r => r.Uri.AbsolutePath == "/api/v3/history");
        await Assert.That(history.Uri.Query).Contains("downloadId=AAAABBBB");
    }

    [Test]
    public async Task Sonarr_HistoryLookup_FallsBackToCallerSpelling()
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (request, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
                (request.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query) switch
                {
                    (_, "/api/v3/queue", _) => """{ "records": [] }""",
                    (_, "/api/v3/history", string q) when q.Contains("downloadId=AAAABBBB") => """{ "records": [] }""",
                    (_, "/api/v3/history", _) =>
                        """{ "records": [ { "id": 21, "eventType": "grabbed", "downloadId": "aaaabbbb", "seriesId": 9, "episodeId": 634 } ] }""",
                    _ => "{}"
                }))
        };

        FailedDownloadResolution result = await Sonarr(handler).ResolveFailedDownload("aaaabbbb");

        await Assert.That(result.Found).IsTrue();
        List<CapturedRequest> historyRequests = handler.Requests
            .Where(r => r.Uri.AbsolutePath == "/api/v3/history")
            .ToList();
        await Assert.That(historyRequests.Count).IsEqualTo(2);
        await Assert.That(historyRequests[1].Uri.Query).Contains("downloadId=aaaabbbb");
    }

    [Test]
    public async Task Radarr_QueueItem_IsBlocklistedAndReSearched()
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (request, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
                (request.Method, request.RequestUri!.AbsolutePath) switch
                {
                    (_, "/api/v3/queue") => """{ "records": [ { "id": 7, "movieId": 10, "downloadId": "AAAABBBB" } ] }""",
                    _ => "{}"
                }))
        };

        FailedDownloadResolution result = await Radarr(handler).ResolveFailedDownload("aaaabbbb");

        await Assert.That(result.Found).IsTrue();
        await Assert.That(result.Blocklisted).IsTrue();
        await Assert.That(result.SearchTriggered).IsTrue();

        CapturedRequest delete = handler.Requests.Single(r => r.Method == HttpMethod.Delete);
        await Assert.That(delete.Uri.AbsolutePath).IsEqualTo("/api/v3/queue/7");
        await Assert.That(delete.Uri.Query).Contains("blocklist=true");
        // The torrent was already removed from the client by Arrk, and a fresh search must run.
        await Assert.That(delete.Uri.Query).Contains("removeFromClient=false");
        await Assert.That(delete.Uri.Query).Contains("skipRedownload=false");
    }

    [Test]
    public async Task Radarr_NoQueueItem_FallsBackToHistory_BlocklistAndSearch()
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (request, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
                (request.Method, request.RequestUri!.AbsolutePath) switch
                {
                    (_, "/api/v3/queue") => """{ "records": [] }""",
                    (_, "/api/v3/history") =>
                        """{ "records": [ { "id": 16, "eventType": "grabbed", "downloadId": "AAAABBBB", "movieId": 15, "sourceTitle": "Some.Release.2014" } ] }""",
                    _ => "{}"
                }))
        };

        FailedDownloadResolution result = await Radarr(handler).ResolveFailedDownload("aaaabbbb");

        await Assert.That(result.Found).IsTrue();
        await Assert.That(result.Blocklisted).IsTrue();
        await Assert.That(result.SearchTriggered).IsTrue();

        await Assert.That(handler.Requests.Any(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/v3/history/failed/16")).IsTrue();

        CapturedRequest search = handler.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/v3/command");
        await Assert.That(search.Body).Contains("MoviesSearch");
        await Assert.That(search.Body).Contains("15");
    }

    [Test]
    public async Task Sonarr_NoQueueItem_FallsBackToHistory_SearchesEpisode()
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (request, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
                (request.Method, request.RequestUri!.AbsolutePath) switch
                {
                    (_, "/api/v3/queue") => """{ "records": [] }""",
                    (_, "/api/v3/history") =>
                        """{ "records": [ { "id": 21, "eventType": "grabbed", "downloadId": "CCCCDDDD", "seriesId": 9, "episodeId": 634 } ] }""",
                    _ => "{}"
                }))
        };

        FailedDownloadResolution result = await Sonarr(handler).ResolveFailedDownload("ccccdddd");

        await Assert.That(result.Found).IsTrue();
        await Assert.That(result.Blocklisted).IsTrue();
        await Assert.That(result.SearchTriggered).IsTrue();

        await Assert.That(handler.Requests.Any(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/v3/history/failed/21")).IsTrue();

        CapturedRequest search = handler.Requests.Single(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/v3/command");
        await Assert.That(search.Body).Contains("EpisodeSearch");
        await Assert.That(search.Body).Contains("634");
    }

    [Test]
    public async Task Radarr_AlreadyFailedHistory_DoesNotRefail_ButStillSearches()
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (request, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
                (request.Method, request.RequestUri!.AbsolutePath) switch
                {
                    (_, "/api/v3/queue") => """{ "records": [] }""",
                    (_, "/api/v3/history") =>
                        """{ "records": [ { "id": 30, "eventType": "downloadFailed", "downloadId": "AAAABBBB", "movieId": 15 } ] }""",
                    _ => "{}"
                }))
        };

        FailedDownloadResolution result = await Radarr(handler).ResolveFailedDownload("aaaabbbb");

        await Assert.That(result.SearchTriggered).IsTrue();
        // The record is already failed/blocklisted; re-marking it would error on the arr.
        await Assert.That(handler.Requests.Any(r => r.Uri.AbsolutePath.Contains("/history/failed/"))).IsFalse();
        await Assert.That(handler.Requests.Any(r =>
            r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/v3/command")).IsTrue();
    }

    [Test]
    public async Task Radarr_UnknownTorrent_ReportsNotFound_WithoutSideEffects()
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse("""{ "records": [] }"""))
        };

        FailedDownloadResolution result = await Radarr(handler).ResolveFailedDownload("deadbeef");

        await Assert.That(result.Found).IsFalse();
        await Assert.That(result.Blocklisted).IsFalse();
        await Assert.That(result.SearchTriggered).IsFalse();
        await Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Delete)).IsFalse();
        await Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Post)).IsFalse();
    }
}
