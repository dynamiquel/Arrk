using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.Core.Notifications;
using Arrk.TestSupport;

namespace Arrk.Notifications.Tests;

public class NotificationPurgeJobTests
{
    static (NotificationPurgeJob Job, NotificationService Service, TestDatabase Db) Create(int retentionDays)
    {
        TestDatabase db = new();
        NotificationConfig config = new() { RetentionDays = retentionDays };
        NotificationService service = new(
            db.Factory,
            new MutableOptionsMonitor<NotificationConfig>(config),
            TestCatalog.Registry(),
            TestNotifications.Subscriptions(db),
            [],
            TimeProvider.System,
            NullLogger<NotificationService>.Instance);
        NotificationPurgeJob job = new(
            service,
            new MutableOptionsMonitor<NotificationConfig>(config),
            TimeProvider.System,
            NullLogger<NotificationPurgeJob>.Instance);
        return (job, service, db);
    }

    static NotificationMessage Message(string title, DateTime timestamp) => new()
    {
        Key = DownloadNotificationTopics.DownloadCompleted,
        Title = title,
        Timestamp = timestamp
    };

    [Test]
    public async Task Execute_RemovesNotificationsOlderThanRetention()
    {
        (NotificationPurgeJob job, NotificationService service, TestDatabase db) = Create(retentionDays: 90);
        await using (db)
        {
            await service.PublishAsync(Message("old", DateTime.UtcNow.AddDays(-120)));
            await service.PublishAsync(Message("recent", DateTime.UtcNow.AddDays(-1)));

            await job.Execute(CancellationToken.None);

            IReadOnlyList<Core.Models.Notification> remaining = await service.QueryAsync(new NotificationQuery());
            await Assert.That(remaining.Count).IsEqualTo(1);
            await Assert.That(remaining[0].Title).IsEqualTo("recent");
        }
    }

    [Test]
    public async Task Execute_WhenRetentionIsZero_KeepsEverything()
    {
        (NotificationPurgeJob job, NotificationService service, TestDatabase db) = Create(retentionDays: 0);
        await using (db)
        {
            await service.PublishAsync(Message("ancient", DateTime.UtcNow.AddYears(-10)));

            await job.Execute(CancellationToken.None);

            await Assert.That(await service.QueryAsync(new NotificationQuery())).IsNotEmpty();
        }
    }
}
