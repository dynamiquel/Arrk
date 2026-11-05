using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.TestSupport;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr.Tests;

/// <summary>
/// The arrs expose failed-download redownload as <c>autoRedownloadFailed</c> under Download
/// Clients → Download Handling. Stackarr keeps it in step with
/// <c>services.tv.failedDownloads.retriggerSearch</c> so a release the arr itself rejects is
/// replaced, not just one Stackarr sees as a failing torrent.
/// The config is a whole-resource PUT, so every other key must survive the round trip.
/// </summary>
public class FailedDownloadHandlingSyncTests
{
    const string SonarrConfig = """
    {
      "downloadClientWorkingFolders": "_UNPACK_|_FAILED_",
      "enableCompletedDownloadHandling": true,
      "autoRedownloadFailed": false,
      "autoRedownloadFailedFromInteractiveSearch": true,
      "id": 1
    }
    """;

    const string RadarrConfig = """
    {
      "downloadClientWorkingFolders": "_UNPACK_|_FAILED_",
      "enableCompletedDownloadHandling": true,
      "checkForFinishedDownloadInterval": 1,
      "autoRedownloadFailed": false,
      "autoRedownloadFailedFromInteractiveSearch": true,
      "id": 1
    }
    """;

    static FakeHttpMessageHandler Handler(string getBody) => new()
    {
        Fallback = (request, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
            request.Method == HttpMethod.Get ? getBody : "{}"))
    };

    [Test]
    public async Task AlreadyEnabled_DoesNotWrite()
    {
        FakeHttpMessageHandler handler = Handler(SonarrConfig.Replace(
            "\"autoRedownloadFailed\": false", "\"autoRedownloadFailed\": true"));
        SonarrClient client = new("http://localhost", "key", handler);

        bool changed = await FailedDownloadHandlingSync.ApplyAsync(
            client, "Sonarr", true, NullLogger.Instance);

        await Assert.That(changed).IsFalse();
        await Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Put)).IsFalse();
    }

    [Test]
    public async Task Disabled_IsEnabledAndOtherFieldsPreserved()
    {
        FakeHttpMessageHandler handler = Handler(SonarrConfig);
        SonarrClient client = new("http://localhost", "key", handler);

        bool changed = await FailedDownloadHandlingSync.ApplyAsync(
            client, "Sonarr", true, NullLogger.Instance);

        await Assert.That(changed).IsTrue();

        CapturedRequest put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        await Assert.That(put.Uri.AbsolutePath).IsEqualTo("/api/v3/config/downloadclient/1");
        await Assert.That(put.Body).Contains("\"autoRedownloadFailed\":true");
        await Assert.That(put.Body).Contains("\"enableCompletedDownloadHandling\":true");
        await Assert.That(put.Body).Contains("\"downloadClientWorkingFolders\":\"_UNPACK_|_FAILED_\"");
        await Assert.That(put.Body).Contains("\"id\":1");

        // Interactive-search redownload is a separate operator preference; leave it untouched.
        await Assert.That(put.Body).Contains("\"autoRedownloadFailedFromInteractiveSearch\":true");
    }

    [Test]
    public async Task Enabled_IsDisabledWhenRetriggerIsOff()
    {
        FakeHttpMessageHandler handler = Handler(RadarrConfig.Replace(
            "\"autoRedownloadFailed\": false", "\"autoRedownloadFailed\": true"));
        RadarrClient client = new("http://localhost", "key", handler);

        bool changed = await FailedDownloadHandlingSync.ApplyAsync(
            client, "Radarr", false, NullLogger.Instance);

        await Assert.That(changed).IsTrue();
        CapturedRequest put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        await Assert.That(put.Body).Contains("\"autoRedownloadFailed\":false");
        // Radarr-only keys must survive the round trip too.
        await Assert.That(put.Body).Contains("\"checkForFinishedDownloadInterval\":1");
    }

    [Test]
    public async Task MissingFlag_IsTreatedAsDisabledAndEnabled()
    {
        FakeHttpMessageHandler handler = Handler("""
        {
          "enableCompletedDownloadHandling": true,
          "id": 1
        }
        """);
        SonarrClient client = new("http://localhost", "key", handler);

        bool changed = await FailedDownloadHandlingSync.ApplyAsync(
            client, "Sonarr", true, NullLogger.Instance);

        await Assert.That(changed).IsTrue();
        await Assert.That(handler.Requests.Single(r => r.Method == HttpMethod.Put).Body)
            .Contains("\"autoRedownloadFailed\":true");
    }

    [Test]
    public async Task Sync_UpdatesBothArrs()
    {
        FakeHttpMessageHandler radarrHandler = Handler(RadarrConfig);
        FakeHttpMessageHandler sonarrHandler = Handler(SonarrConfig);
        FailedDownloadHandlingSync sync = new(
            new RadarrClient("http://localhost", "key", radarrHandler),
            new SonarrClient("http://localhost", "key", sonarrHandler),
            NullLogger<FailedDownloadHandlingSync>.Instance);

        FailedDownloadSyncResult result = await sync.SyncAsync(enabled: true);

        await Assert.That(result.RadarrChanged).IsTrue();
        await Assert.That(result.SonarrChanged).IsTrue();
        await Assert.That(result.Changed).IsTrue();
        await Assert.That(radarrHandler.Requests.Any(r => r.Method == HttpMethod.Put)).IsTrue();
        await Assert.That(sonarrHandler.Requests.Any(r => r.Method == HttpMethod.Put)).IsTrue();
    }

    [Test]
    public async Task Sync_UnreachableArr_DoesNotBlockTheOther()
    {
        FakeHttpMessageHandler radarrHandler = new()
        {
            Fallback = (_, _) => Task.FromException<HttpResponseMessage>(
                new HttpRequestException("radarr down"))
        };
        FakeHttpMessageHandler sonarrHandler = Handler(SonarrConfig);
        FailedDownloadHandlingSync sync = new(
            new RadarrClient("http://localhost", "key", radarrHandler),
            new SonarrClient("http://localhost", "key", sonarrHandler),
            NullLogger<FailedDownloadHandlingSync>.Instance);

        FailedDownloadSyncResult result = await sync.SyncAsync(enabled: true);

        await Assert.That(result.RadarrChanged).IsFalse();
        await Assert.That(result.SonarrChanged).IsTrue();
        await Assert.That(sonarrHandler.Requests.Any(r => r.Method == HttpMethod.Put)).IsTrue();
    }

    [Test]
    public async Task ConfigRoundTrip_ReadsRealShapedPayload()
    {
        FakeHttpMessageHandler handler = Handler(RadarrConfig);
        RadarrClient client = new("http://localhost", "key", handler);

        JsonObject config = await client.GetDownloadClientConfig();

        await Assert.That(config["id"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(config["autoRedownloadFailed"]!.GetValue<bool>()).IsFalse();
        await Assert.That(config["autoRedownloadFailedFromInteractiveSearch"]!.GetValue<bool>()).IsTrue();
    }
}
