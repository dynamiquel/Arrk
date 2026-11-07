using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Notifications;
using Arrk.TestSupport;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Tests;

/// <summary>
/// Verifies that adding a movie or series emits a <c>media_added</c> notification (the "new
/// monitor" alert), using the real Radarr/Sonarr clients against a scripted HTTP transport.
/// </summary>
public class MediaAddedNotificationTests
{
    static RadarrService Radarr(FakeEventStore store) =>
        new(new RadarrClient("http://localhost", "key", Handler("""
            { "id": 10, "title": "The Matrix", "year": 1999, "tmdbId": 603 }
            """)), NullLogger<RadarrService>.Instance, store);

    static SonarrService Sonarr(FakeEventStore store) =>
        new(new SonarrClient("http://localhost", "key", Handler("""
            { "id": 20, "title": "Breaking Bad", "year": 2008, "tvdbId": 81189 }
            """)), NullLogger<SonarrService>.Instance, store);

    static FakeHttpMessageHandler Handler(string createdEntityJson) => new()
    {
        Fallback = (request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            string json = path switch
            {
                "/api/v3/qualityprofile" => """[{ "id": 7, "name": "Any" }]""",
                "/api/v3/rootfolder" => """[{ "id": 1, "path": "/media" }]""",
                "/api/v3/movie" or "/api/v3/series" => createdEntityJson,
                _ => "{}"
            };
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        }
    };

    [Test]
    public async Task AddMovie_EmitsMediaAddedNotification()
    {
        FakeEventStore store = new();
        RadarrService service = Radarr(store);

        await service.SearchAndAddMovie(603);

        NotificationMessage notification = store.Notifications.Single();
        await Assert.That(notification.Key).IsEqualTo(MediaNotificationTopics.MediaAdded);
        await Assert.That(notification.SourceName).IsEqualTo("The Matrix");
        await Assert.That(notification.Audience.Roles).IsEquivalentTo(new[] { "User" });
        await Assert.That(notification.Audience.FollowersOf!.ItemKey).IsEqualTo("tv:movie:tmdb:603");
        await Assert.That(notification.Fields.Any(f => f.Name == "Type" && f.Value == "Movie")).IsTrue();
    }

    [Test]
    public async Task AddMovie_WithoutSearch_StillEmitsNotification()
    {
        FakeEventStore store = new();
        RadarrService service = Radarr(store);

        await service.SearchAndAddMovie(603, searchForMovie: false);

        await Assert.That(store.Notifications.Any(n => n.Key == MediaNotificationTopics.MediaAdded)).IsTrue();
    }

    [Test]
    public async Task AddSeries_EmitsMediaAddedNotification()
    {
        FakeEventStore store = new();
        SonarrService service = Sonarr(store);

        await service.SearchAndAddSeries(81189, "Breaking Bad");

        NotificationMessage notification = store.Notifications.Single();
        await Assert.That(notification.Key).IsEqualTo(MediaNotificationTopics.MediaAdded);
        await Assert.That(notification.SourceName).IsEqualTo("Breaking Bad");
        await Assert.That(notification.Audience.Roles).IsEquivalentTo(new[] { "User" });
        await Assert.That(notification.Audience.FollowersOf!.ItemKey).IsEqualTo("tv:series:tvdb:81189");
    }

    [Test]
    public async Task AddMovie_WithoutEventStore_DoesNotThrow()
    {
        RadarrService service = new(
            new RadarrClient("http://localhost", "key", Handler("""
                { "id": 10, "title": "The Matrix", "year": 1999, "tmdbId": 603 }
                """)),
            NullLogger<RadarrService>.Instance);

        await service.SearchAndAddMovie(603, searchForMovie: false);
    }
}
