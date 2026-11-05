using Stackarr.Core.Enums;
using Stackarr.Core.Playback;

namespace Stackarr.MediaLifecycle.Tests;

public class WatchActivityServiceTests
{
    [Test]
    public async Task RecordsOnePlayPerSession_AndUpdatesProgress()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        WatchActivityService watch = new(db.Factory, db.Time);

        WatchActivityEntry entry = await watch.RecordAsync(new WatchActivityRecord
        {
            Username = "Luna",
            ProviderKey = WatchActivityServices.Tv,
            Domain = new MediaDomain("tv"),
            ItemType = "episode",
            ItemKey = MediaItemKeys.Episode(MediaItemKeys.Series(10, 0), 2, 4),
            ParentItemKey = MediaItemKeys.Series(10, 0),
            Title = "The One",
            Subtitle = "A Show",
            Source = WatchActivitySources.Jellyfin,
            Device = "Living room TV",
            Client = "Jellyfin",
            SessionKey = "session-1",
            StartedAt = db.Time.Now.UtcDateTime
        });

        db.Time.Advance(TimeSpan.FromMinutes(20));
        WatchActivityEntry updated = await watch.RecordAsync(new WatchActivityRecord
        {
            Username = "Luna",
            ProviderKey = WatchActivityServices.Tv,
            Domain = new MediaDomain("tv"),
            ItemType = "episode",
            ItemKey = entry.ItemKey,
            ParentItemKey = entry.ParentItemKey,
            Title = entry.Title,
            Source = WatchActivitySources.Jellyfin,
            SessionKey = "session-1",
            PositionTicks = 500,
            RuntimeTicks = 1000,
            Progress = 0.5,
            LastSeenAt = db.Time.Now.UtcDateTime
        });

        IReadOnlyList<WatchActivityEntry> history = await watch.GetHistoryAsync(new WatchActivityQuery());
        await Assert.That(history.Count).IsEqualTo(1);
        await Assert.That(updated.Progress).IsEqualTo(0.5);
        await Assert.That(updated.Device).IsEqualTo("Living room TV");
        await Assert.That(updated.LastSeenAt).IsEqualTo(db.Time.Now.UtcDateTime);
    }

    [Test]
    public async Task ProgressUpdate_TouchesTheMostRecentPlay_AndMaintainsConsumptionStamps()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        WatchActivityService watch = new(db.Factory, db.Time);

        string trackKey = MediaItemKeys.MusicTrack("Daft Punk", "One More Time");
        string albumKey = MediaItemKeys.MusicAlbum("Daft Punk", "Discovery");
        await watch.RecordAsync(new WatchActivityRecord
        {
            Username = "Luna",
            ProviderKey = WatchActivityServices.Music,
            Domain = new MediaDomain("music"),
            ItemType = "track",
            ItemKey = trackKey,
            ParentItemKey = albumKey,
            Title = "One More Time",
            Subtitle = "Daft Punk",
            Source = WatchActivitySources.Stackarr,
            StartedAt = db.Time.Now.UtcDateTime
        });

        db.Time.Advance(TimeSpan.FromMinutes(2));
        await watch.UpdateProgressAsync("Luna", WatchActivityServices.Music, trackKey, 100, 200, completed: false);

        IReadOnlyDictionary<string, MediaConsumptionStamp> stamps = await watch.GetConsumptionAsync(WatchActivityServices.Music);
        await Assert.That(stamps.ContainsKey(trackKey)).IsTrue();
        await Assert.That(stamps.ContainsKey(albumKey)).IsTrue();
        await Assert.That(stamps[trackKey].LastConsumedAt).IsEqualTo(db.Time.Now.UtcDateTime);

        IReadOnlyDictionary<string, DateTime> last = await watch.GetLastConsumedAsync(
            WatchActivityServices.Music, [albumKey]);
        await Assert.That(last[albumKey]).IsEqualTo(db.Time.Now.UtcDateTime);
    }

    [Test]
    public async Task History_IsFilteredByUserServiceAndSearch()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        WatchActivityService watch = new(db.Factory, db.Time);

        await watch.RecordAsync(new WatchActivityRecord
        {
            Username = "Luna",
            ProviderKey = WatchActivityServices.Tv,
            Domain = new MediaDomain("movie"),
            ItemType = "movie",
            ItemKey = MediaItemKeys.Movie(1),
            Title = "Arrival",
            Source = WatchActivitySources.Jellyfin,
            StartedAt = db.Time.Now.UtcDateTime
        });
        await watch.RecordAsync(new WatchActivityRecord
        {
            Username = "Nova",
            ProviderKey = WatchActivityServices.Music,
            Domain = new MediaDomain("music"),
            ItemType = "track",
            ItemKey = MediaItemKeys.MusicTrack("Artist", "Song"),
            Title = "Song",
            Source = WatchActivitySources.Stackarr,
            StartedAt = db.Time.Now.UtcDateTime
        });

        IReadOnlyList<WatchActivityEntry> luna = await watch.GetHistoryAsync(new WatchActivityQuery { Username = "Luna" });
        IReadOnlyList<WatchActivityEntry> music = await watch.GetHistoryAsync(new WatchActivityQuery { ProviderKey = WatchActivityServices.Music });
        IReadOnlyList<WatchActivityEntry> search = await watch.GetHistoryAsync(new WatchActivityQuery { Search = "arriv" });

        await Assert.That(luna.Count).IsEqualTo(1);
        await Assert.That(music.Count).IsEqualTo(1);
        await Assert.That(music[0].Username).IsEqualTo("Nova");
        await Assert.That(search.Count).IsEqualTo(1);
        await Assert.That(search[0].Title).IsEqualTo("Arrival");
    }

    [Test]
    public async Task CountHistory_FiltersByAccount()
    {
        await using TestDatabase db = await TestDatabase.CreateAsync(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        WatchActivityService watch = new(db.Factory, db.Time);

        foreach ((string username, int userId) in new[] { ("Luna", 1), ("Luna", 1), ("Nova", 2) })
        {
            await watch.RecordAsync(new WatchActivityRecord
            {
                Username = username,
                UserId = userId,
                ProviderKey = WatchActivityServices.Tv,
                Domain = new MediaDomain("tv"),
                ItemType = "movie",
                ItemKey = MediaItemKeys.Movie(userId),
                Title = username,
                Source = WatchActivitySources.Jellyfin,
                StartedAt = db.Time.Now.UtcDateTime
            });
        }

        int luna = await watch.CountHistoryAsync(new WatchActivityQuery { UserId = 1 });
        int nova = await watch.CountHistoryAsync(new WatchActivityQuery { UserId = 2 });

        await Assert.That(luna).IsEqualTo(2);
        await Assert.That(nova).IsEqualTo(1);
    }
}
