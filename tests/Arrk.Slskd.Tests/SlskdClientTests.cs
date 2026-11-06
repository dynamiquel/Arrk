using System.Text.Json;
using Arrk.Slskd;
using Arrk.TestSupport;

namespace Arrk.Slskd.Tests;

/// <summary>Verifies the slskd REST client (v0): JWT login, search, enqueue and download parsing.</summary>
public class SlskdClientTests
{
    static (SlskdClient Client, FakeHttpMessageHandler Handler) Build()
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "jwt-token" }"""
                : path.EndsWith("/server")
                    ? """{ "isLoggedIn": true, "state": "Connected, LoggedIn" }"""
                    : path.Contains("/searches")
                        ? """{ "id": "s1", "isComplete": false, "responses": [] }"""
                        : "[]";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };
        return (new SlskdClient("http://slskd:5030", "arrk", "secret", handler), handler);
    }

    [Test]
    public async Task Ping_LogsIn_ThenQueriesServer()
    {
        (SlskdClient client, FakeHttpMessageHandler handler) = Build();

        bool ok = await client.PingAsync();

        await Assert.That(ok).IsTrue();
        await Assert.That(handler.Requests.Any(r => r.Uri.AbsolutePath.Contains("/api/v0/session"))).IsTrue();
        await Assert.That(handler.Requests.Any(r => r.Uri.AbsolutePath.EndsWith("/api/v0/server"))).IsTrue();
        // The server call carries the bearer token from the login.
        await Assert.That(handler.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/api/v0/server")).AuthParameter)
            .IsEqualTo("jwt-token");
    }

    [Test]
    public async Task Ping_ReLogsIn_AfterA401DiscardsTheCachedToken()
    {
        // slskd invalidates the JWT when it restarts; a 401 must drop the cached token so the next call
        // logs in again instead of reporting "Soulseek unavailable" forever.
        int logins = 0;
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path.Contains("/session"))
            {
                logins++;
                return Task.FromResult(FakeHttpMessageHandler.JsonResponse($$"""{ "token": "jwt-{{logins}}" }"""));
            }
            if (path.EndsWith("/server"))
            {
                return Task.FromResult(logins <= 1
                    ? FakeHttpMessageHandler.JsonResponse("{}", System.Net.HttpStatusCode.Unauthorized)
                    : FakeHttpMessageHandler.JsonResponse("""{ "isLoggedIn": true }"""));
            }
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse("[]"));
        };
        SlskdClient client = new("http://slskd:5030", "arrk", "secret", handler);

        bool first = await client.PingAsync();
        bool second = await client.PingAsync();

        await Assert.That(first).IsFalse();
        await Assert.That(second).IsTrue();
        await Assert.That(logins).IsEqualTo(2);
    }

    [Test]
    public async Task StartSearch_PostsSearchText()
    {
        (SlskdClient client, FakeHttpMessageHandler handler) = Build();

        string? id = await client.StartSearchAsync("Ella Langley Choosin' Texas");

        await Assert.That(id).IsEqualTo("s1");
        CapturedRequest search = handler.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/api/v0/searches"));
        await Assert.That(search.Method.Method).IsEqualTo("POST");
        await Assert.That(search.Body).Contains("Ella Langley");
    }

    [Test]
    public async Task Enqueue_PostsFilesToPeer()
    {
        (SlskdClient client, FakeHttpMessageHandler handler) = Build();

        bool ok = await client.EnqueueAsync("peer1", [new SlskdFile { Filename = "Music\\Song.flac", Size = 123 }]);

        await Assert.That(ok).IsTrue();
        CapturedRequest request = handler.Requests.Last(r => r.Uri.AbsolutePath.Contains("/transfers/downloads/peer1"));
        await Assert.That(request.Method.Method).IsEqualTo("POST");
        await Assert.That(request.Body).Contains("Song.flac");
    }

    [Test]
    public async Task GetDownloads_FlattensFileTree()
    {
        (SlskdClient client, FakeHttpMessageHandler handler) = Build();
        handler.Fallback = (req, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
            req.RequestUri!.AbsolutePath.EndsWith("/transfers/downloads")
                ? """
                  [ { "username": "peer1", "directories": [ { "directory": "Music",
                      "files": [ { "filename": "Music\\Song.flac", "state": "Completed, Succeeded", "percentComplete": 100, "size": 500 } ] } ] } ]
                  """
                : """{ "token": "jwt-token" }"""));

        List<SlskdDownload> downloads = await client.GetDownloadsAsync();

        await Assert.That(downloads).HasCount(1);
        await Assert.That(downloads[0].Username).IsEqualTo("peer1");
        await Assert.That(downloads[0].IsComplete).IsTrue();
    }

    [Test]
    public async Task GetDownloads_ParsesIdStateAndTimestamps()
    {
        (SlskdClient client, FakeHttpMessageHandler handler) = Build();
        handler.Fallback = (req, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
            req.RequestUri!.AbsolutePath.EndsWith("/transfers/downloads")
                ? """
                  [ { "username": "peer1", "directories": [ { "directory": "Music\\Album",
                      "files": [ { "id": "abc", "username": "peer1", "filename": "Music\\Album\\Song.flac",
                        "state": "Queued, Remotely", "percentComplete": 0, "size": 1000, "bytesTransferred": 0,
                        "enqueuedAt": "2026-10-15T19:48:23.851337" } ] } ] } ]
                  """
                : """{ "token": "jwt-token" }"""));

        List<SlskdDownload> downloads = await client.GetDownloadsAsync();

        await Assert.That(downloads).HasCount(1);
        await Assert.That(downloads[0].Id).IsEqualTo("abc");
        await Assert.That(downloads[0].DirectoryName).IsEqualTo("Album");
        await Assert.That(downloads[0].FileName).IsEqualTo("Song.flac");
        await Assert.That(downloads[0].IsQueued).IsTrue();
        await Assert.That(downloads[0].IsFailed).IsFalse();
        await Assert.That(downloads[0].EnqueuedAt).IsNotNull();
    }

    [Test]
    public async Task GetDownloads_FailedStatesAreDetected()
    {
        (SlskdClient client, FakeHttpMessageHandler handler) = Build();
        handler.Fallback = (req, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
            req.RequestUri!.AbsolutePath.EndsWith("/transfers/downloads")
                ? """
                  [ { "username": "peer1", "directories": [ { "directory": "Music\\Album", "files": [
                      { "id": "a", "filename": "Music\\Album\\1.flac", "state": "Completed, Aborted", "percentComplete": 40, "size": 100, "bytesTransferred": 40 },
                      { "id": "b", "filename": "Music\\Album\\2.flac", "state": "Completed, Rejected", "percentComplete": 0, "size": 100, "bytesTransferred": 0 },
                      { "id": "c", "filename": "Music\\Album\\3.flac", "state": "Completed, Errored", "percentComplete": 0, "size": 100, "bytesTransferred": 0, "exception": "peer went away" } ] } ] } ]
                  """
                : """{ "token": "jwt-token" }"""));

        List<SlskdDownload> downloads = await client.GetDownloadsAsync();

        await Assert.That(downloads).HasCount(3);
        await Assert.That(downloads.All(d => d.IsFailed)).IsTrue();
        await Assert.That(downloads.All(d => !d.IsComplete)).IsTrue();
    }

    [Test]
    public async Task CancelDownload_DeletesWithRemoveFlag()
    {
        (SlskdClient client, FakeHttpMessageHandler handler) = Build();

        bool ok = await client.CancelDownloadAsync("peer1", "abc", remove: true);

        await Assert.That(ok).IsTrue();
        CapturedRequest request = handler.Requests.Last(r => r.Method.Method == "DELETE");
        await Assert.That(request.Uri.AbsolutePath).Contains("/transfers/downloads/peer1/abc");
        await Assert.That(request.Uri.Query).Contains("remove=true");
    }

    [Test]
    public async Task SetTransferLimits_PatchesOptionsOverlay()
    {
        (SlskdClient client, FakeHttpMessageHandler handler) = Build();

        bool ok = await client.SetTransferLimitsAsync(uploadSpeedKib: 2048, downloadSpeedKib: 0);

        await Assert.That(ok).IsTrue();
        CapturedRequest request = handler.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/api/v0/options"));
        await Assert.That(request.Method.Method).IsEqualTo("PATCH");
        // The configured upload cap is applied; 0 means unlimited → slskd's max value.
        await Assert.That(request.Body).Contains("\"speedLimit\":2048");
        await Assert.That(request.Body).Contains("\"speedLimit\":2147483647");
    }

    [Test]
    public async Task CredentialsAndUrl_AreResolvedPerCall_SoConfigEditsApply()
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (req, _) =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("/session")
                ? """{ "token": "jwt" }"""
                : """{ "isLoggedIn": true, "state": "Connected, LoggedIn" }""";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        string url = "http://slskd:5030";
        string username = "first";
        SlskdClient client = new(() => url, () => (username, "pw"), handler);

        await client.PingAsync();

        username = "second";
        url = "http://gluetun:5030";
        await client.PingAsync();

        List<CapturedRequest> logins = [.. handler.Requests.Where(r => r.Uri.AbsolutePath.Contains("/session"))];
        await Assert.That(logins.Count).IsEqualTo(2);
        await Assert.That(logins[0].Body).Contains("first");
        await Assert.That(logins[1].Body).Contains("second");
        await Assert.That(handler.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/server")).Uri.Host).IsEqualTo("gluetun");
    }
}
