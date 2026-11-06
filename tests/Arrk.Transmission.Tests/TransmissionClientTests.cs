using System.Net;
using System.Text;
using System.Text.Json;
using Arrk.Core.Services;

using Arrk.TestSupport;
namespace Arrk.Transmission.Tests;

public class TransmissionClientTests
{
    const string Url = "http://localhost:9091/";
    const string User = "transmission";
    const string Password = "arrk";

    static TransmissionClient CreateClient(FakeHttpMessageHandler handler) => new(Url, User, Password, handler);

    static string SessionGet(string version = "4.0.5") =>
        "{\"result\":\"success\",\"arguments\":{\"version\":\"" + version + "\"}}";

    static string TorrentsJson(string torrents) =>
        "{\"result\":\"success\",\"arguments\":{\"torrents\":[" + torrents + "]}}";

    static JsonElement Body(CapturedRequest request) =>
        JsonDocument.Parse(request.Body).RootElement;

    static string AuthHeader() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));

    // Request construction / auth / session

    [Test]
    public async Task SendRequest_SetsBasicAuthHeader()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(SessionGet());
        using TransmissionClient client = CreateClient(handler);

        await client.TestConnection();

        await Assert.That(handler.Requests).HasSingleItem();
        await Assert.That(handler.Requests[0].AuthParameter).IsEqualTo(AuthHeader());
        await Assert.That(handler.Requests[0].Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.Requests[0].Uri.AbsolutePath).IsEqualTo("/transmission/rpc");
    }

    [Test]
    public async Task DynamicBaseUrl_IsResolvedPerRequest()
    {
        // Mirrors toggling VPN routing at runtime: the same client should follow the host
        // returned by the provider on each call.
        string host = "stackarr-demo-transmission";
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(SessionGet());
        handler.EnqueueJson(SessionGet());
        using TransmissionClient client = new(() => $"http://{host}:9091", User, Password, handler);

        await client.TestConnection();
        host = "stackarr-demo-gluetun";
        await client.TestConnection();

        await Assert.That(handler.Requests.Count).IsEqualTo(2);
        await Assert.That(handler.Requests[0].Uri.Host).IsEqualTo("stackarr-demo-transmission");
        await Assert.That(handler.Requests[1].Uri.Host).IsEqualTo("stackarr-demo-gluetun");
    }

    [Test]
    public async Task SendRequest_FirstRequest_HasNoSessionHeader()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(SessionGet());
        using TransmissionClient client = CreateClient(handler);

        await client.GetSession();

        await Assert.That(handler.Requests[0].SessionId).IsNull();
    }

    [Test]
    public async Task SendRequest_OnConflict_RetriesWithSessionIdFromHeader()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueStatus(HttpStatusCode.Conflict, sessionId: "session-abc");
        handler.EnqueueJson(SessionGet());
        using TransmissionClient client = CreateClient(handler);

        TransmissionSession session = await client.GetSession();

        await Assert.That(session.Version).IsEqualTo("4.0.5");
        await Assert.That(handler.Requests.Count).IsEqualTo(2);
        await Assert.That(handler.Requests[0].SessionId).IsNull();
        await Assert.That(handler.Requests[1].SessionId).IsEqualTo("session-abc");
    }

    [Test]
    public async Task SendRequest_OnConflictWithoutSessionHeader_StillRetries()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueStatus(HttpStatusCode.Conflict);
        handler.EnqueueJson(SessionGet());
        using TransmissionClient client = CreateClient(handler);

        TransmissionSession session = await client.GetSession();

        await Assert.That(session.Version).IsEqualTo("4.0.5");
        await Assert.That(handler.Requests[1].SessionId).IsNull();
    }

    [Test]
    public async Task SendRequest_NullJsonBody_ThrowsInvalidOperationException()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("null");
        using TransmissionClient client = CreateClient(handler);

        await Assert.That(async () => await client.GetSession())
            .Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(TransmissionTorrentSort.Id, "id")]
    [Arguments(TransmissionTorrentSort.Name, "name")]
    [Arguments(TransmissionTorrentSort.AddedDate, "addedDate")]
    [Arguments(TransmissionTorrentSort.RateDownload, "rateDownload")]
    [Arguments(TransmissionTorrentSort.RateUpload, "rateUpload")]
    [Arguments(TransmissionTorrentSort.UploadRatio, "uploadRatio")]
    [Arguments(TransmissionTorrentSort.Progress, "percentDone")]
    [Arguments(TransmissionTorrentSort.QueuePosition, "queuePosition")]
    public async Task GetTorrents_MapsSortField(TransmissionTorrentSort sort, string expected)
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(TorrentsJson(""));
        using TransmissionClient client = CreateClient(handler);

        await client.GetTorrents(sort: sort);

        JsonElement args = Body(handler.Requests[0]).GetProperty("arguments");
        await Assert.That(args.GetProperty("sort").GetString()).IsEqualTo(expected);
    }

    [Test]
    public async Task GetTorrents_ExplicitFields_AreSent()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(TorrentsJson(""));
        using TransmissionClient client = CreateClient(handler);

        await client.GetTorrents(fields: ["id", "name"]);

        JsonElement fields = Body(handler.Requests[0]).GetProperty("arguments").GetProperty("fields");
        await Assert.That(fields.GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task ResetSession_ClearsStoredSessionId()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueStatus(HttpStatusCode.Conflict, sessionId: "s1");
        handler.EnqueueJson(SessionGet());
        handler.EnqueueJson(SessionGet());
        using TransmissionClient client = CreateClient(handler);

        await client.GetSession();
        client.ResetSession();
        await client.GetSession();

        await Assert.That(handler.Requests[1].SessionId).IsEqualTo("s1");
        await Assert.That(handler.Requests[2].SessionId).IsNull();
    }

    // Error handling

    [Test]
    public async Task SendRequest_NonSuccessStatus_ThrowsHttpRequestException()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("{}", HttpStatusCode.InternalServerError);
        using TransmissionClient client = CreateClient(handler);

        await Assert.That(async () => await client.GetSession())
            .Throws<HttpRequestException>();
    }

    [Test]
    public async Task SendRequest_RpcErrorResult_ThrowsTransmissionException()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"no such method"}""");
        using TransmissionClient client = CreateClient(handler);

        await Assert.That(async () => await client.GetSession())
            .Throws<TransmissionException>();
    }

    [Test]
    public async Task SendRequest_InvalidJson_ThrowsJsonException()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("this is not json");
        using TransmissionClient client = CreateClient(handler);

        await Assert.That(async () => await client.GetSession())
            .Throws<JsonException>();
    }

    [Test]
    public async Task TestConnection_OnTransportFailure_ReturnsFalse()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueThrow(new HttpRequestException("connection refused"));
        using TransmissionClient client = CreateClient(handler);

        bool connected = await client.TestConnection();

        await Assert.That(connected).IsFalse();
    }

    [Test]
    public async Task TestConnection_OnSuccess_ReturnsTrue()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(SessionGet());
        using TransmissionClient client = CreateClient(handler);

        await Assert.That(await client.TestConnection()).IsTrue();
    }

    // Session / stats

    [Test]
    public async Task GetSession_ReturnsParsedSession()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success","arguments":{"version":"4.1.0","download-dir":"/downloads","download-queue-size":7}}""");
        using TransmissionClient client = CreateClient(handler);

        TransmissionSession session = await client.GetSession();

        await Assert.That(session.Version).IsEqualTo("4.1.0");
        await Assert.That(session.DownloadDir).IsEqualTo("/downloads");
        await Assert.That(session.DownloadQueueSize).IsEqualTo(7);
    }

    [Test]
    public async Task GetSessionStats_ReturnsParsedStats()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success","arguments":{"downloadSpeed":111,"uploadSpeed":222,"torrentCount":3,"cumulative-stats":{"downloadedBytes":1000,"uploadedBytes":2000}}}""");
        using TransmissionClient client = CreateClient(handler);

        TransmissionSessionStats stats = await client.GetSessionStats();

        await Assert.That(stats.DownloadSpeed).IsEqualTo(111);
        await Assert.That(stats.UploadSpeed).IsEqualTo(222);
        await Assert.That(stats.TorrentCount).IsEqualTo(3);
        await Assert.That(stats.CumulativeStats!.DownloadedBytes).IsEqualTo(1000);
    }

    // GetTorrents / filtering

    [Test]
    public async Task GetTorrents_NoFilter_SendsFieldsAndSort()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(TorrentsJson(""));
        using TransmissionClient client = CreateClient(handler);

        await client.GetTorrents(sort: TransmissionTorrentSort.Name, descending: true);

        JsonElement body = Body(handler.Requests[0]);
        await Assert.That(body.GetProperty("method").GetString()).IsEqualTo("torrent-get");
        JsonElement args = body.GetProperty("arguments");
        await Assert.That(args.GetProperty("sort").GetString()).IsEqualTo("name");
        await Assert.That(args.GetProperty("sortDescending").GetBoolean()).IsTrue();
        await Assert.That(args.GetProperty("fields").EnumerateArray().Any(f => f.GetString() == "hashString")).IsTrue();
        await Assert.That(args.TryGetProperty("ids", out _)).IsFalse();
    }

    static async Task<int[]> RunFilterAsync(Dictionary<string, bool> filter, string allTorrentsJson)
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(TorrentsJson(allTorrentsJson));   // filter discovery call
        handler.EnqueueJson(TorrentsJson(""));                // actual torrent-get
        using TransmissionClient client = CreateClient(handler);

        await client.GetTorrents(filter: filter);

        JsonElement ids = Body(handler.Requests[1]).GetProperty("arguments").GetProperty("ids");
        return ids.EnumerateArray().Select(e => e.GetInt32()).ToArray();
    }

    const string MixedTorrents =
        """{"id":1,"status":4,"error":0},""" +
        """{"id":2,"status":6,"error":0},""" +
        """{"id":3,"status":0,"error":0},""" +
        """{"id":4,"status":4,"error":1},""" +
        """{"id":5,"status":3,"error":0},""" +
        """{"id":6,"status":5,"error":0}""";

    [Test]
    public async Task GetTorrents_FilterDownloading_ReturnsDownloadingAndWait()
    {
        int[] ids = await RunFilterAsync(new() { ["downloading"] = true }, MixedTorrents);
        await Assert.That(ids).IsEquivalentTo([1, 4, 5]);
    }

    [Test]
    public async Task GetTorrents_FilterSeeding_ReturnsSeedingAndWait()
    {
        int[] ids = await RunFilterAsync(new() { ["seeding"] = true }, MixedTorrents);
        await Assert.That(ids).IsEquivalentTo([2, 6]);
    }

    [Test]
    public async Task GetTorrents_FilterPaused_ReturnsStopped()
    {
        int[] ids = await RunFilterAsync(new() { ["paused"] = true }, MixedTorrents);
        await Assert.That(ids).IsEquivalentTo([3]);
    }

    [Test]
    public async Task GetTorrents_FilterError_ReturnsErrored_RequiresErrorField()
    {
        int[] ids = await RunFilterAsync(new() { ["error"] = true }, MixedTorrents);
        await Assert.That(ids).IsEquivalentTo([4]);
    }

    [Test]
    public async Task GetTorrents_FilterDiscovery_RequestsErrorField()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(TorrentsJson(MixedTorrents));
        handler.EnqueueJson(TorrentsJson(""));
        using TransmissionClient client = CreateClient(handler);

        await client.GetTorrents(filter: new() { ["error"] = true });

        JsonElement fields = Body(handler.Requests[0]).GetProperty("arguments").GetProperty("fields");
        await Assert.That(fields.EnumerateArray().Any(f => f.GetString() == "error")).IsTrue();
    }

    [Test]
    public async Task GetTorrents_FilterActive_ReturnsDownloadingAndSeeding()
    {
        int[] ids = await RunFilterAsync(new() { ["active"] = true }, MixedTorrents);
        await Assert.That(ids).IsEquivalentTo([1, 2, 4]);
    }

    [Test]
    public async Task GetTorrents_FilterQueued_ReturnsQueuedStates()
    {
        int[] ids = await RunFilterAsync(new() { ["queued"] = true }, MixedTorrents);
        await Assert.That(ids).IsEquivalentTo([5, 6]);
    }

    [Test]
    public async Task GetTorrents_FilterNoMatch_ReturnsEmptyAndSendsEmptyIds()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(TorrentsJson("""{"id":2,"status":6,"error":0}"""));
        handler.EnqueueJson(TorrentsJson(""));
        using TransmissionClient client = CreateClient(handler);

        List<TransmissionTorrent> torrents = await client.GetTorrents(filter: new() { ["error"] = true });

        await Assert.That(torrents).IsEmpty();
        JsonElement ids = Body(handler.Requests[1]).GetProperty("arguments").GetProperty("ids");
        await Assert.That(ids.GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task GetTorrentById_ReturnsMatchingTorrent()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(TorrentsJson("""{"id":7,"status":4,"error":0}"""));
        handler.EnqueueJson(TorrentsJson("""{"id":7,"status":4,"error":0}"""));
        using TransmissionClient client = CreateClient(handler);

        TransmissionTorrent? torrent = await client.GetTorrentById(7);

        await Assert.That(torrent).IsNotNull();
        await Assert.That(torrent!.Id).IsEqualTo(7);
    }

    // Add

    [Test]
    public async Task AddTorrent_SendsBase64MetainfoDirectoryAndLabels()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success","arguments":{"torrent-added":{"id":42,"name":"x","hashString":"h"}}}""");
        using TransmissionClient client = CreateClient(handler);

        long id = await client.AddTorrent([1, 2, 3], "/downloads", ["movies"]);

        await Assert.That(id).IsEqualTo(42);
        JsonElement args = Body(handler.Requests[0]).GetProperty("arguments");
        await Assert.That(args.GetProperty("metainfo").GetString()).IsEqualTo(Convert.ToBase64String([1, 2, 3]));
        await Assert.That(args.GetProperty("download-dir").GetString()).IsEqualTo("/downloads");
        await Assert.That(args.GetProperty("labels").EnumerateArray().First().GetString()).IsEqualTo("movies");
    }

    [Test]
    public async Task AddTorrent_Duplicate_ReturnsDuplicateId()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success","arguments":{"torrent-duplicate":{"id":9,"name":"dup","hashString":"h"}}}""");
        using TransmissionClient client = CreateClient(handler);

        long id = await client.AddTorrent([1]);

        await Assert.That(id).IsEqualTo(9);
    }

    [Test]
    public async Task AddTorrent_NoTorrentInResponse_ReturnsZero()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success","arguments":{}}""");
        using TransmissionClient client = CreateClient(handler);

        await Assert.That(await client.AddTorrent([1])).IsEqualTo(0);
    }

    [Test]
    public async Task AddTorrentByUri_SendsFilename()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success","arguments":{"torrent-added":{"id":3,"name":"x","hashString":"h"}}}""");
        using TransmissionClient client = CreateClient(handler);

        long id = await client.AddTorrentByUri(new Uri("magnet:?xt=urn:btih:abc"), "/downloads");

        await Assert.That(id).IsEqualTo(3);
        JsonElement args = Body(handler.Requests[0]).GetProperty("arguments");
        await Assert.That(args.GetProperty("filename").GetString()).IsEqualTo("magnet:?xt=urn:btih:abc");
    }

    // Remove / pause / resume

    [Test]
    public async Task RemoveTorrent_SendsHashAndDeleteLocalData()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success"}""");
        using TransmissionClient client = CreateClient(handler);

        await client.RemoveTorrent("hash123");

        JsonElement body = Body(handler.Requests[0]);
        await Assert.That(body.GetProperty("method").GetString()).IsEqualTo("torrent-remove");
        JsonElement args = body.GetProperty("arguments");
        await Assert.That(args.GetProperty("ids").EnumerateArray().First().GetString()).IsEqualTo("hash123");
        await Assert.That(args.GetProperty("delete-local-data").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task RemoveTorrents_KeepFiles_SetsDeleteLocalDataFalse()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success"}""");
        using TransmissionClient client = CreateClient(handler);

        await client.RemoveTorrents(["a", "b"], TorrentFileCleanup.KeepFiles);

        JsonElement args = Body(handler.Requests[0]).GetProperty("arguments");
        await Assert.That(args.GetProperty("ids").GetArrayLength()).IsEqualTo(2);
        await Assert.That(args.GetProperty("delete-local-data").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task PauseAndResume_SendCorrectMethods()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success"}""");
        handler.EnqueueJson("""{"result":"success"}""");
        using TransmissionClient client = CreateClient(handler);

        await client.PauseTorrent("h1");
        await client.ResumeTorrent("h1");

        await Assert.That(Body(handler.Requests[0]).GetProperty("method").GetString()).IsEqualTo("torrent-stop");
        await Assert.That(Body(handler.Requests[1]).GetProperty("method").GetString()).IsEqualTo("torrent-start");
        await Assert.That(Body(handler.Requests[0]).GetProperty("arguments").GetProperty("ids").EnumerateArray().First().GetString()).IsEqualTo("h1");
    }

    [Test]
    public async Task PauseAllAndResumeAll_SendNoIds()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success"}""");
        handler.EnqueueJson("""{"result":"success"}""");
        using TransmissionClient client = CreateClient(handler);

        await client.PauseAllTorrents();
        await client.ResumeAllTorrents();

        await Assert.That(Body(handler.Requests[0]).TryGetProperty("arguments", out _)).IsFalse();
        await Assert.That(Body(handler.Requests[1]).TryGetProperty("arguments", out _)).IsFalse();
    }

    [Test]
    public async Task Reannounce_SendsIds()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success"}""");
        using TransmissionClient client = CreateClient(handler);

        await client.Reannounce(5);

        JsonElement body = Body(handler.Requests[0]);
        await Assert.That(body.GetProperty("method").GetString()).IsEqualTo("torrent-reannounce");
        await Assert.That(body.GetProperty("arguments").GetProperty("ids").EnumerateArray().First().GetInt32()).IsEqualTo(5);
    }

    [Test]
    public async Task SetTorrentLocation_SendsLocationAndMove()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success"}""");
        using TransmissionClient client = CreateClient(handler);

        await client.SetTorrentLocation(5, "/newdir");

        JsonElement args = Body(handler.Requests[0]).GetProperty("arguments");
        await Assert.That(args.GetProperty("location").GetString()).IsEqualTo("/newdir");
        await Assert.That(args.GetProperty("move").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task SetTorrentBandwidthPriority_SendsPriority()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success"}""");
        using TransmissionClient client = CreateClient(handler);

        await client.SetTorrentBandwidthPriority(5, 1);

        JsonElement args = Body(handler.Requests[0]).GetProperty("arguments");
        await Assert.That(args.GetProperty("bandwidth-priority").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task SetTorrentQueuePosition_SendsPosition()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success"}""");
        using TransmissionClient client = CreateClient(handler);

        await client.SetTorrentQueuePosition(5, 3);

        JsonElement args = Body(handler.Requests[0]).GetProperty("arguments");
        await Assert.That(args.GetProperty("queue-position").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task GetQueuePosition_ReturnsPositionForTorrent()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson(TorrentsJson("""{"id":5,"queuePosition":2}"""));
        using TransmissionClient client = CreateClient(handler);

        int[] positions = await client.GetQueuePosition(5);

        await Assert.That(positions).IsEquivalentTo([2]);
    }

    // Files / trackers

    [Test]
    public async Task GetTorrentFiles_MapsFilesAndStats()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success","arguments":{"torrents":[{"id":1,"files":[{"name":"a.mkv","length":100,"bytesCompleted":40},{"name":"b.srt","length":10,"bytesCompleted":10}],"fileStats":[{"wanted":true,"priority":1,"bytesCompleted":40},{"wanted":false,"priority":0,"bytesCompleted":10}]}]}}""");
        using TransmissionClient client = CreateClient(handler);

        List<TransmissionFileEntry> files = await client.GetTorrentFiles(1);

        await Assert.That(files.Count).IsEqualTo(2);
        await Assert.That(files[0].Index).IsEqualTo(0);
        await Assert.That(files[0].Name).IsEqualTo("a.mkv");
        await Assert.That(files[0].Length).IsEqualTo(100);
        await Assert.That(files[0].Wanted).IsTrue();
        await Assert.That(files[1].Wanted).IsFalse();
        await Assert.That(files[1].Priority).IsEqualTo(0);
    }

    [Test]
    public async Task GetTorrentFiles_MissingFileStats_DefaultsToWanted()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success","arguments":{"torrents":[{"id":1,"files":[{"name":"a.mkv","length":100,"bytesCompleted":40}]}]}}""");
        using TransmissionClient client = CreateClient(handler);

        List<TransmissionFileEntry> files = await client.GetTorrentFiles(1);

        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0].Wanted).IsTrue();
    }

    [Test]
    public async Task GetTorrentFiles_NoFiles_ReturnsEmpty()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success","arguments":{"torrents":[{"id":1}]}}""");
        using TransmissionClient client = CreateClient(handler);

        await Assert.That(await client.GetTorrentFiles(1)).IsEmpty();
    }

    [Test]
    public async Task GetTorrentTrackers_ReturnsTrackers()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success","arguments":{"torrents":[{"id":1,"trackers":[{"id":1,"announce":"http://tracker/announce","lastAnnounceSucceeded":true}]}]}}""");
        using TransmissionClient client = CreateClient(handler);

        List<TransmissionTrackerEntry> trackers = await client.GetTorrentTrackers(1);

        await Assert.That(trackers.Count).IsEqualTo(1);
        await Assert.That(trackers[0].Announce).IsEqualTo("http://tracker/announce");
        await Assert.That(trackers[0].LastAnnounceSucceeded).IsTrue();
    }

    // Session limits

    [Test]
    public async Task SetSessionLimits_SendsOnlyProvidedValues_AndReturnsSession()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success"}""");
        handler.EnqueueJson(SessionGet("4.0.5"));
        using TransmissionClient client = CreateClient(handler);

        TransmissionSession session = await client.SetSessionLimits(
            uploadLimit: 1024,
            uploadLimitEnabled: true,
            maxActiveDownloads: 5,
            maxActiveSeeds: 10);

        JsonElement args = Body(handler.Requests[0]).GetProperty("arguments");
        await Assert.That(args.GetProperty("speed-limit-up").GetInt32()).IsEqualTo(1024);
        await Assert.That(args.GetProperty("speed-limit-up-enabled").GetBoolean()).IsTrue();
        await Assert.That(args.GetProperty("download-queue-size").GetInt32()).IsEqualTo(5);
        await Assert.That(args.GetProperty("download-queue-enabled").GetBoolean()).IsTrue();
        await Assert.That(args.GetProperty("seed-queue-size").GetInt32()).IsEqualTo(10);
        // Transmission's RPC keys are hyphenated; the underscore form is silently ignored and
        // leaves the seed queue disabled (so the size limit is never enforced).
        await Assert.That(args.GetProperty("seed-queue-enabled").GetBoolean()).IsTrue();
        await Assert.That(args.TryGetProperty("seed_queue_enabled", out _)).IsFalse();
        // Arrk never pushes seed-ratio/idle limits to Transmission; seeding goals are enforced
        // by the download-management cleanup instead.
        await Assert.That(args.TryGetProperty("seed_ratio_limit", out _)).IsFalse();
        await Assert.That(args.TryGetProperty("idle_seeding_limit", out _)).IsFalse();
        await Assert.That(args.TryGetProperty("speed-limit-down", out _)).IsFalse();
        await Assert.That(session.Version).IsEqualTo("4.0.5");
    }

    // Labels

    [Test]
    public async Task GetLabels_DeduplicatesAcrossTorrents()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"result":"success","arguments":{"torrents":[{"id":1,"labels":["movies","hd"]},{"id":2,"labels":["movies","tv"]},{"id":3}]}}""");
        using TransmissionClient client = CreateClient(handler);

        List<string> labels = await client.GetLabels();

        await Assert.That(labels).Contains("movies");
        await Assert.That(labels).Contains("hd");
        await Assert.That(labels).Contains("tv");
        await Assert.That(labels.Count).IsEqualTo(3);
    }

    // Timeout / cancellation

    [Test]
    public async Task SendRequest_WhenCancelled_ThrowsOperationCanceled()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueDelay(TimeSpan.FromSeconds(30), SessionGet());
        using TransmissionClient client = CreateClient(handler);

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(50));

        await Assert.That(async () => await client.GetSession(cts.Token))
            .Throws<OperationCanceledException>();
    }

    // Concurrency

    [Test]
    public async Task SendRequest_ConcurrentCalls_AllSucceed()
    {
        FakeHttpMessageHandler handler = new()
        {
            Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(SessionGet("4.0.5")))
        };
        using TransmissionClient client = CreateClient(handler);

        IEnumerable<Task<TransmissionSession>> tasks = Enumerable.Range(0, 50)
            .Select(_ => client.GetSession());

        TransmissionSession[] sessions = await Task.WhenAll(tasks);

        await Assert.That(sessions.Length).IsEqualTo(50);
        await Assert.That(sessions.All(s => s.Version == "4.0.5")).IsTrue();
    }

    [Test]
    public async Task SendRequest_ConcurrentConflicts_RecoverSessionId()
    {
        FakeHttpMessageHandler handler = new();
        for (int i = 0; i < 10; i++)
        {
            handler.EnqueueStatus(HttpStatusCode.Conflict, sessionId: "sess");
            handler.EnqueueJson(SessionGet());
        }
        using TransmissionClient client = CreateClient(handler);

        IEnumerable<Task<TransmissionSession>> tasks = Enumerable.Range(0, 10)
            .Select(_ => client.GetSession());

        TransmissionSession[] sessions = await Task.WhenAll(tasks);

        await Assert.That(sessions.All(s => s.Version == "4.0.5")).IsTrue();
    }
}
