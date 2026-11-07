using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Notifications;
using Arrk.TestSupport;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Tests;

/// <summary>
/// Verifies the media import monitor announces a <c>downloads.imported</c> notification when an arr
/// history record shows a completed import, using the real Radarr/Sonarr clients against a scripted
/// HTTP transport. Covers the checkpoint (first run only announces recent imports; already-announced
/// imports are not repeated) and season-pack grouping.
/// </summary>
public class MediaImportMonitorTests
{
    static string Recent => DateTime.UtcNow.AddMinutes(-5).ToString("o");
    const string Old = "2020-01-01T00:00:00Z";

    static MediaImportMonitor Monitor(FakeEventStore store, RadarrService radarr, SonarrService sonarr, IEventBus? bus = null) =>
        new(radarr, sonarr, store, bus ?? new EventBus(), NullLogger<MediaImportMonitor>.Instance);

    static RadarrService Radarr(FakeEventStore store, string history, string movie) =>
        new(new RadarrClient("http://localhost", "key", Handler(
                ("/api/v3/history", history),
                ("/api/v3/movie/", movie))),
            NullLogger<RadarrService>.Instance, store);

    static SonarrService Sonarr(FakeEventStore store, string history, string series, string episodes) =>
        new(new SonarrClient("http://localhost", "key", Handler(
                ("/api/v3/history", history),
                ("/api/v3/series/", series),
                ("/api/v3/episodefile", "[]"),
                ("/api/v3/episode", episodes))),
            NullLogger<SonarrService>.Instance, store);

    static FakeHttpMessageHandler Handler(params (string PathPrefix, string Json)[] routes) => new()
    {
        Fallback = (request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            foreach ((string prefix, string json) in routes)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
            }

            return Task.FromResult(FakeHttpMessageHandler.JsonResponse("{}"));
        }
    };

    static async Task SeedCheckpointAsync(FakeEventStore store, string source) =>
        await store.RecordAsync(new SystemEventRecord
        {
            Category = EventCategory.Media,
            EventType = MediaImportMonitor.ImportEventType,
            Message = "prior import",
            Source = source
        });

    static string RadarrHistory(int id, string date, string eventType = "downloadFolderImported", int movieId = 18) =>
        $$"""
        { "page": 1, "pageSize": 200, "totalRecords": 1, "records": [
            { "id": {{id}}, "eventType": "{{eventType}}", "date": "{{date}}",
              "sourceTitle": "Batman.Knightfall.2026.1080p", "movieId": {{movieId}} } ] }
        """;

    const string MovieJson = """
        { "id": 18, "title": "Batman Knightfall", "year": 2026, "tmdbId": 100,
          "images": [ { "coverType": "poster", "remoteUrl": "https://img/poster.jpg" },
                      { "coverType": "fanart", "remoteUrl": "https://img/fanart.jpg" } ] }
        """;

    [Test]
    public async Task FirstPoll_AnnouncesOnlyRecentImports_NotTheWholeLibrary()
    {
        FakeEventStore store = new();
        string history = $$"""
            { "page": 1, "pageSize": 200, "totalRecords": 2, "records": [
                { "id": 27, "eventType": "downloadFolderImported", "date": "{{Recent}}",
                  "sourceTitle": "Batman.Knightfall.2026.1080p", "movieId": 18 },
                { "id": 20, "eventType": "downloadFolderImported", "date": "{{Old}}",
                  "sourceTitle": "Love.Rosie.2014.1080p", "movieId": 15 } ] }
            """;
        MediaImportMonitor monitor = Monitor(store, Radarr(store, history, MovieJson), Sonarr(store, "{}", "{}", "[]"));

        await monitor.PollAsync();

        NotificationMessage notification = store.Notifications.Single();
        await Assert.That(notification.Body).Contains("Batman Knightfall");
    }

    [Test]
    public async Task NewImport_AnnouncesMovieWithArt()
    {
        FakeEventStore store = new();
        await SeedCheckpointAsync(store, "radarr:25");
        MediaImportMonitor monitor = Monitor(
            store, Radarr(store, RadarrHistory(26, Recent), MovieJson), Sonarr(store, "{}", "{}", "[]"));

        await monitor.PollAsync();

        NotificationMessage notification = store.Notifications.Single();
        await Assert.That(notification.Key).IsEqualTo(DownloadNotificationTopics.DownloadImported);
        await Assert.That(notification.Title).IsEqualTo("Movie imported");
        await Assert.That(notification.Body).Contains("Batman Knightfall");
        await Assert.That(notification.Body).Contains("now available in your library");
        await Assert.That(notification.Style!.ThumbnailUrl).IsEqualTo("https://img/poster.jpg");
        await Assert.That(notification.Audience).IsEqualTo(NotificationAudience.BroadcastAndSubscribers);
        await Assert.That(notification.Entity).IsNotNull();
        await Assert.That(notification.Entity!.ItemKey).IsEqualTo("tv:movie:tmdb:100");

        SystemEventRecord record = store.Records.Last(r => r.Notification is not null);
        await Assert.That(record.Source).IsEqualTo("radarr:26");
        await Assert.That(record.EventType).IsEqualTo(MediaImportMonitor.ImportEventType);
    }

    [Test]
    public async Task NewImport_PublishesMediaImportedEvent()
    {
        FakeEventStore store = new();
        await SeedCheckpointAsync(store, "radarr:25");
        EventBus bus = new();
        List<MediaImportedEvent> received = [];
        bus.Subscribe<MediaImportedEvent>((e, _) =>
        {
            received.Add(e);
            return ValueTask.CompletedTask;
        });

        MediaImportMonitor monitor = Monitor(
            store, Radarr(store, RadarrHistory(26, Recent), MovieJson), Sonarr(store, "{}", "{}", "[]"), bus);

        await monitor.PollAsync();

        MediaImportedEvent imported = received.Single();
        await Assert.That(imported.Source).IsEqualTo("radarr:26");
        await Assert.That(imported.ItemType).IsEqualTo("Movie");
        await Assert.That(imported.MovieId).IsEqualTo(18);
        await Assert.That(imported.Entity).IsNotNull();
        await Assert.That(imported.Entity!.ItemKey).IsEqualTo("tv:movie:tmdb:100");
    }

    [Test]
    public async Task AlreadyAnnouncedImport_IsNotRepeated()
    {
        FakeEventStore store = new();
        await SeedCheckpointAsync(store, "radarr:26");
        MediaImportMonitor monitor = Monitor(
            store, Radarr(store, RadarrHistory(26, Recent), MovieJson), Sonarr(store, "{}", "{}", "[]"));

        await monitor.PollAsync();

        await Assert.That(store.Notifications).IsEmpty();
    }

    [Test]
    public async Task GrabWithoutImport_DoesNotAnnounce()
    {
        FakeEventStore store = new();
        await SeedCheckpointAsync(store, "radarr:25");
        MediaImportMonitor monitor = Monitor(
            store, Radarr(store, RadarrHistory(26, Recent, eventType: "grabbed"), MovieJson),
            Sonarr(store, "{}", "{}", "[]"));

        await monitor.PollAsync();

        await Assert.That(store.Notifications).IsEmpty();
    }

    [Test]
    public async Task SonarrSeasonPack_AnnouncesOnceForTheRange()
    {
        FakeEventStore store = new();
        await SeedCheckpointAsync(store, "sonarr:100");

        string history = """
            { "page": 1, "pageSize": 200, "totalRecords": 3, "records": [
                { "id": 101, "eventType": "downloadFolderImported", "date": "2026-10-17T20:56:09Z",
                  "sourceTitle": "[Group] Re.Zero S01", "downloadId": "abc", "seriesId": 10, "episodeId": 1 },
                { "id": 102, "eventType": "downloadFolderImported", "date": "2026-10-17T20:56:09Z",
                  "sourceTitle": "[Group] Re.Zero S01", "downloadId": "abc", "seriesId": 10, "episodeId": 2 },
                { "id": 103, "eventType": "downloadFolderImported", "date": "2026-10-17T20:56:09Z",
                  "sourceTitle": "[Group] Re.Zero S01", "downloadId": "abc", "seriesId": 10, "episodeId": 3 } ] }
            """;
        const string seriesJson = """
            { "id": 10, "title": "Re:Zero", "tvdbId": 5,
              "images": [ { "coverType": "poster", "remoteUrl": "https://img/rz.jpg" } ] }
            """;
        const string episodesJson = """
            [ { "id": 1, "seriesId": 10, "seasonNumber": 1, "episodeNumber": 1, "title": "One", "hasFile": true },
              { "id": 2, "seriesId": 10, "seasonNumber": 1, "episodeNumber": 2, "title": "Two", "hasFile": true },
              { "id": 3, "seriesId": 10, "seasonNumber": 1, "episodeNumber": 3, "title": "Three", "hasFile": true } ]
            """;

        SonarrService sonarr = Sonarr(store, history, seriesJson, episodesJson);
        MediaImportMonitor monitor = Monitor(store, Radarr(store, "{}", "{}"), sonarr);

        await monitor.PollAsync();

        NotificationMessage notification = store.Notifications.Single();
        await Assert.That(notification.Key).IsEqualTo(DownloadNotificationTopics.DownloadImported);
        await Assert.That(notification.Title).IsEqualTo("Episode imported");
        await Assert.That(notification.Body).Contains("Re:Zero");
        await Assert.That(notification.Body).Contains("S01E01–E03");

        SystemEventRecord record = store.Records.Last(r => r.Notification is not null);
        await Assert.That(record.Source).IsEqualTo("sonarr:103");
    }
}
