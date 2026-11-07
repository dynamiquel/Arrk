using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Music.Services;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Notifications;
using Arrk.Music;

namespace Arrk.Music.Tests;

/// <summary>
/// Verifies the music producer emits a single <see cref="SystemEventRecord"/> carrying a
/// notification (topic, body and cover art) for grabs, imports and adds. The store is a lightweight
/// capture double: the projection itself is covered by NotifyingEventStore tests.
/// </summary>
public class MusicNotificationsTests
{
    static Album Album(string poster = "") => new()
    {
        Id = 7,
        Title = "Halo 3",
        ArtistName = "Martin O’Donnell",
        PosterUrl = poster
    };

    static (MusicNotifications Notifications, CapturingEventStore Store) Build()
    {
        CapturingEventStore store = new();
        MusicNotifications notifications = new(store, deezer: null, NullLogger<MusicNotifications>.Instance);
        return (notifications, store);
    }

    [Test]
    public async Task AlbumGrabbed_RecordsNotificationWithCoverArt()
    {
        (MusicNotifications notifications, CapturingEventStore store) = Build();

        await notifications.AlbumGrabbedAsync(Album("https://images.example/halo3.jpg"), "Halo 3 ODST [FLAC]");

        SystemEventRecord record = store.Records.Single();
        await Assert.That(record.Topic).IsEqualTo(MusicNotificationTopics.AlbumGrabbed);
        await Assert.That(record.Notification).IsNotNull();
        await Assert.That(record.Notification!.Key).IsEqualTo(MusicNotificationTopics.AlbumGrabbed);
        await Assert.That(record.Notification.Body).Contains("Halo 3 ODST");
        await Assert.That(record.Notification.Style).IsNotNull();
        await Assert.That(record.Notification.Style!.ImageUrl).IsEqualTo("https://images.example/halo3.jpg");
    }

    [Test]
    public async Task AlbumGrabbed_RelativeArt_IsDropped()
    {
        (MusicNotifications notifications, CapturingEventStore store) = Build();

        await notifications.AlbumGrabbedAsync(Album("/api/music/art/album?title=Halo"), "Halo 3 ODST");

        SystemEventRecord record = store.Records.Single();
        await Assert.That(record.Notification!.Style).IsNull();
    }

    [Test]
    public async Task AlbumImported_RecordsSuccessNotification()
    {
        (MusicNotifications notifications, CapturingEventStore store) = Build();

        await notifications.AlbumImportedAsync(Album(), "Halo 3 ODST", placed: 12);

        SystemEventRecord record = store.Records.Single();
        await Assert.That(record.Topic).IsEqualTo(MusicNotificationTopics.AlbumImported);
        await Assert.That(record.Severity).IsEqualTo(EventSeverity.Success);
        await Assert.That(record.Notification!.Fields.Single(f => f.Name == "Artist").Value).IsEqualTo("Martin O’Donnell");
        await Assert.That(record.Notification.Fields.Single(f => f.Name == "Files").Value).IsEqualTo("12");
    }

    [Test]
    public async Task Imported_SingleTrack_IsReportedAsASongWithTrackArtistAlbumAndArt()
    {
        (MusicNotifications notifications, CapturingEventStore store) = Build();
        Track track = new() { Id = 1, Title = "Let It Go" };

        await notifications.ImportedAsync(Album("https://images.example/letitgo.jpg"), "Let It Go", [track]);

        SystemEventRecord record = store.Records.Single();
        await Assert.That(record.Topic).IsEqualTo(MusicNotificationTopics.SongImported);
        await Assert.That(record.EventType).IsEqualTo("music.song.imported");
        await Assert.That(record.Notification!.Title).IsEqualTo("Song imported");
        await Assert.That(record.Notification.Body).Contains("Let It Go");
        await Assert.That(record.Notification.Body).Contains("Halo 3");
        await Assert.That(record.Notification.Fields.Single(f => f.Name == "Track").Value).IsEqualTo("Let It Go");
        await Assert.That(record.Notification.Fields.Single(f => f.Name == "Artist").Value).IsEqualTo("Martin O’Donnell");
        await Assert.That(record.Notification.Fields.Single(f => f.Name == "Album").Value).IsEqualTo("Halo 3");
        await Assert.That(record.Notification.Style!.ImageUrl).IsEqualTo("https://images.example/letitgo.jpg");
    }

    [Test]
    public async Task Imported_MultipleTracks_IsReportedAsAnAlbum()
    {
        (MusicNotifications notifications, CapturingEventStore store) = Build();
        Track one = new() { Id = 1, Title = "One" };
        Track two = new() { Id = 2, Title = "Two" };

        await notifications.ImportedAsync(Album(), "Halo 3", [one, two]);

        SystemEventRecord record = store.Records.Single();
        await Assert.That(record.Topic).IsEqualTo(MusicNotificationTopics.AlbumImported);
        await Assert.That(record.Notification!.Fields.Single(f => f.Name == "Files").Value).IsEqualTo("2");
    }

    [Test]
    public async Task ArtistAdded_RecordsNotification()
    {
        (MusicNotifications notifications, CapturingEventStore store) = Build();

        await notifications.ArtistAddedAsync("Radiohead");

        SystemEventRecord record = store.Records.Single();
        await Assert.That(record.Topic).IsEqualTo(MusicNotificationTopics.ArtistAdded);
        await Assert.That(record.Notification!.Body).Contains("Radiohead");
    }

    [Test]
    public async Task AlbumDownloadFailed_RecordsWarningWithReason()
    {
        (MusicNotifications notifications, CapturingEventStore store) = Build();

        await notifications.AlbumDownloadFailedAsync(Album(), "Halo 3 ODST [FLAC]", "stalled");

        SystemEventRecord record = store.Records.Single();
        await Assert.That(record.Topic).IsEqualTo(DownloadNotificationTopics.MusicAlbumDownloadFailed);
        await Assert.That(record.Severity).IsEqualTo(EventSeverity.Warning);
        await Assert.That(record.Notification!.Fields.Single(f => f.Name == "Reason").Value).IsEqualTo("stalled");
    }

    [Test]
    public async Task SoulseekGrabbed_RecordsNotification()
    {
        (MusicNotifications notifications, CapturingEventStore store) = Build();

        await notifications.SoulseekGrabbedAsync("Idina Menzel", "Let It Go", "peer1", "lossless");

        SystemEventRecord record = store.Records.Single();
        await Assert.That(record.Topic).IsEqualTo(DownloadNotificationTopics.SoulseekGrabbed);
        await Assert.That(record.Notification).IsNotNull();
        await Assert.That(record.Notification!.Body).Contains("peer1");
        await Assert.That(record.Notification.Fields.Single(f => f.Name == "Quality").Value).IsEqualTo("lossless");
    }

    [Test]
    public async Task SearchMissed_RecordsAuditOnlyEvent()
    {
        (MusicNotifications notifications, CapturingEventStore store) = Build();

        await notifications.SearchMissedAsync("Idina Menzel", "Let It Go", "No matching Soulseek files");

        SystemEventRecord record = store.Records.Single();
        await Assert.That(record.EventType).IsEqualTo("music.release.missed");
        await Assert.That(record.Notification).IsNull();     // audit-only: Events page, not the bell
        await Assert.That(record.Message).Contains("No matching Soulseek files");
    }

    sealed class CapturingEventStore : IEventStore
    {
        public List<SystemEventRecord> Records { get; } = [];

        public Task RecordAsync(SystemEventRecord record, CancellationToken ct = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task RecordAsync(IEnumerable<SystemEventRecord> records, CancellationToken ct = default)
        {
            Records.AddRange(records);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SystemEvent>> QueryAsync(SystemEventQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SystemEvent>>([]);

        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);

        public Task<IReadOnlyList<string>> GetEventTypesAsync(string? category = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
