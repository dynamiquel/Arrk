using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Notifications;
using Arrk.TestSupport;

namespace Arrk.Notifications.Tests;

public class NotifyingEventStoreTests
{
    static SystemEventRecord Record(string message, NotificationMessage? notification = null) => new()
    {
        Category = EventCategory.Download,
        EventType = "torrent.added",
        Message = message,
        Source = "hash1",
        SourceName = "Ubuntu",
        Notification = notification,
        Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    static NotificationMessage Notification(
        string topic = DownloadNotificationTopics.TorrentAdded,
        string title = "Torrent added") => new()
        {
            Key = topic,
            Title = title,
            Body = "body",
            Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

    static NotificationService Service(TestDatabase db) => new(
        db.Factory,
        new MutableOptionsMonitor<NotificationConfig>(new NotificationConfig()),
        TestCatalog.Registry(),
        new NotificationAudienceResolver(new TestUserDirectory().ScopeFactory(), TestNotifications.Subscriptions(db)),
        TestNotifications.Subscriptions(db),
        [],
        TimeProvider.System,
        NullLogger<NotificationService>.Instance);

    [Test]
    public async Task Record_WithNotification_PersistsAuditAndInbox()
    {
        TestDatabase auditDb = new();
        TestDatabase notificationDb = new();
        await using (auditDb)
        await using (notificationDb)
        {
            EfEventStore inner = new(auditDb.Factory);
            NotificationService notifications = Service(notificationDb);
            NotifyingEventStore store = new(inner, [notifications], NullLogger<NotifyingEventStore>.Instance);

            await store.RecordAsync(Record("added", Notification()));

            IReadOnlyList<SystemEvent> audit = await inner.QueryAsync(new SystemEventQuery());
            await Assert.That(audit.Count).IsEqualTo(1);
            IReadOnlyList<Notification> inbox = await notifications.QueryAsync(new NotificationQuery());
            await Assert.That(inbox.Count).IsEqualTo(1);
            await Assert.That(inbox[0].Key).IsEqualTo(DownloadNotificationTopics.TorrentAdded);
        }
    }

    [Test]
    public async Task Record_WithoutNotification_PersistsAuditOnly()
    {
        TestDatabase auditDb = new();
        TestDatabase notificationDb = new();
        await using (auditDb)
        await using (notificationDb)
        {
            EfEventStore inner = new(auditDb.Factory);
            NotificationService notifications = Service(notificationDb);
            NotifyingEventStore store = new(inner, [notifications], NullLogger<NotifyingEventStore>.Instance);

            await store.RecordAsync(Record("audit only"));

            IReadOnlyList<SystemEvent> audit = await inner.QueryAsync(new SystemEventQuery());
            await Assert.That(audit.Count).IsEqualTo(1);
            await Assert.That(await notifications.QueryAsync(new NotificationQuery())).IsEmpty();
        }
    }

    [Test]
    public async Task Record_Batch_NotifiesForEachAttachedNotification()
    {
        TestDatabase auditDb = new();
        TestDatabase notificationDb = new();
        await using (auditDb)
        await using (notificationDb)
        {
            EfEventStore inner = new(auditDb.Factory);
            NotificationService notifications = Service(notificationDb);
            NotifyingEventStore store = new(inner, [notifications], NullLogger<NotifyingEventStore>.Instance);

            await store.RecordAsync([
                Record("a", Notification(DownloadNotificationTopics.TorrentAdded, "A")),
                Record("b"),
                Record("c", Notification(DownloadNotificationTopics.DownloadCompleted, "C"))
            ]);

            IReadOnlyList<SystemEvent> audit = await inner.QueryAsync(new SystemEventQuery());
            await Assert.That(audit.Count).IsEqualTo(3);
            IReadOnlyList<Notification> inbox = await notifications.QueryAsync(new NotificationQuery());
            await Assert.That(inbox.Count).IsEqualTo(2);
            await Assert.That(inbox.Select(n => n.Key)).Contains(DownloadNotificationTopics.DownloadCompleted);
        }
    }

    [Test]
    public async Task Record_WhenNotificationPipelineFails_AuditStillPersists()
    {
        TestDatabase auditDb = new();
        TestDatabase notificationDb = new();
        await using (auditDb)
        {
            EfEventStore inner = new(auditDb.Factory);

            // A disposed database makes the notification service throw on persist.
            NotificationService notifications = Service(notificationDb);
            await notificationDb.DisposeAsync();

            NotifyingEventStore store = new(inner, [notifications], NullLogger<NotifyingEventStore>.Instance);

            await store.RecordAsync(Record("survives", Notification()));

            IReadOnlyList<SystemEvent> audit = await inner.QueryAsync(new SystemEventQuery());
            await Assert.That(audit.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Record_WhenInnerStoreFails_Propagates()
    {
        TestDatabase auditDb = new();
        EfEventStore inner = new(auditDb.Factory);
        NotificationService notifications = Service(auditDb);
        NotifyingEventStore store = new(inner, [notifications], NullLogger<NotifyingEventStore>.Instance);

        await auditDb.DisposeAsync();

        await Assert.That(async () => await store.RecordAsync(Record("boom", Notification())))
            .Throws<Exception>();
    }

    [Test]
    public async Task Query_Purge_And_EventTypes_DelegateToInnerStore()
    {
        TestDatabase auditDb = new();
        await using (auditDb)
        {
            EfEventStore inner = new(auditDb.Factory);
            NotifyingEventStore store = new(inner, [], NullLogger<NotifyingEventStore>.Instance);

            await inner.RecordAsync(new SystemEventRecord
            {
                Category = EventCategory.Health,
                EventType = "service.down",
                Message = "down",
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });

            IReadOnlyList<SystemEvent> queried = await store.QueryAsync(new SystemEventQuery { Category = EventCategory.Health });
            IReadOnlyList<string> types = await store.GetEventTypesAsync(EventCategory.Health);
            int purged = await store.PurgeOlderThanAsync(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            await Assert.That(queried.Count).IsEqualTo(1);
            await Assert.That(types).Contains("service.down");
            await Assert.That(purged).IsEqualTo(1);
        }
    }
}
