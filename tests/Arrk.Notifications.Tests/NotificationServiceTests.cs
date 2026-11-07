using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Core.Notifications;
using Arrk.TestSupport;

namespace Arrk.Notifications.Tests;

public class NotificationServiceTests
{
    static NotificationMessage Message(
        string topic = TestNotificationTopics.Success,
        string title = "Download completed",
        string body = "Something finished",
        EventSeverity severity = EventSeverity.Success,
        string? source = "hash1",
        string? sourceName = "Ubuntu") => new()
        {
            Key = topic,
            Title = title,
            Body = body,
            Severity = severity,
            Source = source,
            SourceName = sourceName,
            Timestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        };

    static (NotificationService Service, TestDatabase Db) Create(
        NotificationConfig? config = null,
        IEnumerable<INotificationChannel>? channels = null)
    {
        TestDatabase db = new();
        NotificationService service = new(
            db.Factory,
            new MutableOptionsMonitor<NotificationConfig>(config ?? new NotificationConfig()),
            TestCatalog.Registry(),
            TestNotifications.Subscriptions(db),
            channels ?? [],
            TimeProvider.System,
            NullLogger<NotificationService>.Instance);
        return (service, db);
    }

    [Test]
    public async Task Publish_UnknownTopic_ReturnsNullAndPersistsNothing()
    {
        (NotificationService service, TestDatabase db) = Create();
        await using (db)
        {
            Notification? result = await service.PublishAsync(Message(topic: "nope"));

            await Assert.That(result).IsNull();
            await Assert.That(await service.QueryAsync(new NotificationQuery())).IsEmpty();
        }
    }

    [Test]
    public async Task Publish_TopicDisabledByToggle_ReturnsNull()
    {
        NotificationConfig config = new();
        config.Topics[TestNotificationTopics.Success] = false;
        (NotificationService service, TestDatabase db) = Create(config);
        await using (db)
        {
            Notification? result = await service.PublishAsync(Message());

            await Assert.That(result).IsNull();
            await Assert.That(await service.QueryAsync(new NotificationQuery())).IsEmpty();
        }
    }

    [Test]
    public async Task Publish_DomainDisabled_ReturnsNull()
    {
        NotificationConfig config = new();
        config.Topics["test"] = false;
        (NotificationService service, TestDatabase db) = Create(config);
        await using (db)
        {
            Notification? result = await service.PublishAsync(Message());

            await Assert.That(result).IsNull();
        }
    }

    [Test]
    public async Task Publish_Enabled_PersistsUnreadNotificationWithGroupCategory()
    {
        (NotificationService service, TestDatabase db) = Create();
        await using (db)
        {
            Notification? result = await service.PublishAsync(Message());

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.Category).IsEqualTo("test");
            await Assert.That(result.Severity).IsEqualTo("Success");
            await Assert.That(result.ReadAt).IsNull();
            await Assert.That(result.Delivered).IsFalse();

            IReadOnlyList<Notification> stored = await service.QueryAsync(new NotificationQuery());
            await Assert.That(stored.Count).IsEqualTo(1);
            await Assert.That(stored[0].Title).IsEqualTo("Download completed");
            await Assert.That(stored[0].SourceName).IsEqualTo("Ubuntu");
        }
    }

    [Test]
    public async Task Publish_GroupedTopic_StoresGroupAsCategory()
    {
        (NotificationService service, TestDatabase db) = Create();
        await using (db)
        {
            Notification? result = await service.PublishAsync(
                Message(topic: TestNotificationTopics.GroupAlpha, title: "Alpha", severity: EventSeverity.Error));

            await Assert.That(result!.Category).IsEqualTo("test.group");
        }
    }

    [Test]
    public async Task Publish_NoChannels_LeavesDeliveredFalse()
    {
        (NotificationService service, TestDatabase db) = Create();
        await using (db)
        {
            Notification? result = await service.PublishAsync(Message());

            await Assert.That(result!.Delivered).IsFalse();
            await Assert.That(result.Channels).IsNull();
            await Assert.That(result.Error).IsNull();
        }
    }

    [Test]
    public async Task UnreadCount_And_MarkRead_BehaveCorrectly()
    {
        (NotificationService service, TestDatabase db) = Create();
        await using (db)
        {
            Notification first = (await service.PublishAsync(Message()))!;
            await service.PublishAsync(Message(topic: TestNotificationTopics.Info, title: "Info"));

            await Assert.That(await service.UnreadCountAsync()).IsEqualTo(2);

            Notification? read = await service.MarkReadAsync(first.Id);
            await Assert.That(read!.ReadAt).IsNotNull();
            await Assert.That(await service.UnreadCountAsync()).IsEqualTo(1);
        }
    }

    [Test]
    public async Task MarkRead_MissingId_ReturnsNull()
    {
        (NotificationService service, TestDatabase db) = Create();
        await using (db)
        {
            await Assert.That(await service.MarkReadAsync(9999)).IsNull();
        }
    }

    [Test]
    public async Task MarkAllRead_UpdatesEveryUnreadRow()
    {
        (NotificationService service, TestDatabase db) = Create();
        await using (db)
        {
            await service.PublishAsync(Message());
            await service.PublishAsync(Message(topic: TestNotificationTopics.Info, title: "Info"));

            int updated = await service.MarkAllReadAsync();

            await Assert.That(updated).IsEqualTo(2);
            await Assert.That(await service.UnreadCountAsync()).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Query_Category_MatchesTopicPrefix()
    {
        (NotificationService service, TestDatabase db) = Create();
        await using (db)
        {
            await service.PublishAsync(Message());
            await service.PublishAsync(Message(topic: TestNotificationTopics.GroupAlpha, title: "Alpha"));

            IReadOnlyList<Notification> grouped = await service.QueryAsync(new NotificationQuery
            {
                Category = "test.group"
            });

            await Assert.That(grouped.Count).IsEqualTo(1);
            await Assert.That(grouped[0].Key).IsEqualTo(TestNotificationTopics.GroupAlpha);
        }
    }

    [Test]
    public async Task Query_FiltersByKey()
    {
        (NotificationService service, TestDatabase db) = Create();
        await using (db)
        {
            await service.PublishAsync(Message());
            await service.PublishAsync(Message(topic: TestNotificationTopics.Info, title: "Info"));

            IReadOnlyList<Notification> info = await service.QueryAsync(new NotificationQuery
            {
                Key = TestNotificationTopics.Info
            });

            await Assert.That(info.Count).IsEqualTo(1);
            await Assert.That(info[0].Title).IsEqualTo("Info");
        }
    }

    [Test]
    public async Task Query_UnreadOnly_ExcludesReadRows()
    {
        (NotificationService service, TestDatabase db) = Create();
        await using (db)
        {
            Notification first = (await service.PublishAsync(Message()))!;
            await service.PublishAsync(Message(topic: TestNotificationTopics.Info, title: "Info"));
            await service.MarkReadAsync(first.Id);

            IReadOnlyList<Notification> unread = await service.QueryAsync(new NotificationQuery { UnreadOnly = true });

            await Assert.That(unread.Count).IsEqualTo(1);
            await Assert.That(unread[0].Title).IsEqualTo("Info");
        }
    }

    [Test]
    public async Task Query_Search_MatchesTitleAndBody()
    {
        (NotificationService service, TestDatabase db) = Create();
        await using (db)
        {
            await service.PublishAsync(Message(title: "Movie imported", body: "The Matrix is ready"));
            await service.PublishAsync(Message(topic: TestNotificationTopics.Info, title: "Info", body: "Ubuntu"));

            IReadOnlyList<Notification> byBody = await service.QueryAsync(new NotificationQuery { Search = "Matrix" });
            IReadOnlyList<Notification> byTitle = await service.QueryAsync(new NotificationQuery { Search = "Info" });

            await Assert.That(byBody.Count).IsEqualTo(1);
            await Assert.That(byBody[0].Title).IsEqualTo("Movie imported");
            await Assert.That(byTitle.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Query_RespectsLimit_And_OrdersNewestFirst()
    {
        (NotificationService service, TestDatabase db) = Create();
        await using (db)
        {
            for (int i = 0; i < 5; i++)
            {
                await service.PublishAsync(new NotificationMessage
                {
                    Key = TestNotificationTopics.Info,
                    Title = $"n{i}",
                    Timestamp = new DateTime(2026, 1, 1, 0, i, 0, DateTimeKind.Utc)
                });
            }

            IReadOnlyList<Notification> page = await service.QueryAsync(new NotificationQuery { Limit = 2 });

            await Assert.That(page.Count).IsEqualTo(2);
            await Assert.That(page[0].Title).IsEqualTo("n4");
            await Assert.That(page[1].Title).IsEqualTo("n3");
        }
    }

    [Test]
    public async Task PurgeOlderThan_RemovesOnlyOldRows()
    {
        (NotificationService service, TestDatabase db) = Create();
        await using (db)
        {
            await service.PublishAsync(new NotificationMessage
            {
                Key = TestNotificationTopics.Info,
                Title = "old",
                Timestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await service.PublishAsync(new NotificationMessage
            {
                Key = TestNotificationTopics.Info,
                Title = "new",
                Timestamp = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            });

            int removed = await service.PurgeOlderThanAsync(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            await Assert.That(removed).IsEqualTo(1);
            IReadOnlyList<Notification> remaining = await service.QueryAsync(new NotificationQuery());
            await Assert.That(remaining.Count).IsEqualTo(1);
            await Assert.That(remaining[0].Title).IsEqualTo("new");
        }
    }

    [Test]
    public async Task Publish_ChannelSkipsViaFilter_StaysInInboxButIsNotDelivered()
    {
        (NotificationService service, TestDatabase db) = Create(channels: [new SkippingChannel()]);
        await using (db)
        {
            Notification? result = await service.PublishAsync(Message());

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.Delivered).IsFalse();
            await Assert.That(result.Channels).IsNull();
            await Assert.That(result.Error).IsNull();
            IReadOnlyList<Notification> stored = await service.QueryAsync(new NotificationQuery());
            await Assert.That(stored.Count).IsEqualTo(1);
        }
    }

    sealed class SkippingChannel : INotificationChannel
    {
        public string Name => "skipping";

        public bool IsConfigured => true;

        public Task<NotificationDeliveryResult> SendAsync(
            NotificationMessage message, CancellationToken ct = default) =>
            Task.FromResult(NotificationDeliveryResult.Skip());
    }

    sealed class RecordingChannel : INotificationChannel
    {
        public string Name => "recording";
        public bool IsConfigured => true;
        public bool SupportsDirectDelivery => true;

        public NotificationDeliveryResult DirectResult { get; init; } = NotificationDeliveryResult.Ok();
        public List<NotificationMessage> Broadcast { get; } = [];
        public List<(int UserId, NotificationMessage Message)> Direct { get; } = [];

        public Task<NotificationDeliveryResult> SendAsync(
            NotificationMessage message, CancellationToken ct = default)
        {
            Broadcast.Add(message);
            return Task.FromResult(NotificationDeliveryResult.Ok());
        }

        public Task<NotificationDeliveryResult> SendToUserAsync(
            int userId, NotificationMessage message, CancellationToken ct = default)
        {
            Direct.Add((userId, message));
            return Task.FromResult(DirectResult);
        }
    }

    static MediaEntityRef MovieEntity() => new(
        "tv", new MediaDomain("movie"), "tv:movie:tmdb:100", "The Matrix");

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Public_entity_notification_reaches_global_channels_and_only_matching_followers(
        bool hasFollowers, bool directFails)
    {
        RecordingChannel channel = new()
        {
            DirectResult = directFails
                ? NotificationDeliveryResult.Fail("DMs are blocked")
                : NotificationDeliveryResult.Ok()
        };
        (NotificationService service, TestDatabase db) = Create(channels: [channel]);
        await using (db)
        {
            NotificationSubscriptionService subscriptions = TestNotifications.Subscriptions(db);
            if (hasFollowers)
            {
                await subscriptions.SubscribeAsync(7, MovieEntity(), new NotificationSubscriptionOptions());
                await subscriptions.SubscribeAsync(8, MovieEntity(), new NotificationSubscriptionOptions
                {
                    NewContent = false
                });
                await subscriptions.SubscribeAsync(9, MovieEntity() with { ItemKey = "tv:movie:tmdb:200" },
                    new NotificationSubscriptionOptions());
            }

            Notification? result = await service.PublishAsync(new NotificationMessage
            {
                Key = DownloadNotificationTopics.DownloadImported,
                Title = "Movie imported",
                Entity = MovieEntity(),
                Audience = NotificationAudience.BroadcastAndSubscribers
            });

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.UserId).IsNull();
            await Assert.That(result.Delivered).IsTrue();
            await Assert.That(channel.Broadcast.Count).IsEqualTo(1);
            await Assert.That(channel.Direct.Count).IsEqualTo(hasFollowers ? 1 : 0);
            IReadOnlyList<Notification> inbox = await service.QueryAsync(new NotificationQuery());
            await Assert.That(inbox.Count).IsEqualTo(1);
            await Assert.That(inbox[0].Delivered).IsTrue();
            await Assert.That(inbox[0].Error).IsNull();
            await Assert.That(await service.UnreadCountAsync(7)).IsEqualTo(1);
            if (hasFollowers)
                await Assert.That(channel.Direct[0].UserId).IsEqualTo(7);
        }
    }

    [Test]
    public async Task Explicit_user_notification_with_entity_never_reaches_global_channels_or_other_followers()
    {
        RecordingChannel channel = new();
        (NotificationService service, TestDatabase db) = Create(channels: [channel]);
        await using (db)
        {
            await TestNotifications.Subscriptions(db).SubscribeAsync(
                8, MovieEntity(), new NotificationSubscriptionOptions());

            Notification? result = await service.PublishToUserAsync(7, new NotificationMessage
            {
                Key = DownloadNotificationTopics.DownloadImported,
                Title = "Your movie is available",
                Entity = MovieEntity(),
                Audience = NotificationAudience.BroadcastAndSubscribers
            });

            await Assert.That(result!.UserId).IsEqualTo(7);
            await Assert.That(channel.Broadcast).IsEmpty();
            await Assert.That(channel.Direct.Count).IsEqualTo(1);
            await Assert.That(channel.Direct[0].UserId).IsEqualTo(7);
            await Assert.That((await service.QueryAsync(new NotificationQuery())).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Subscriber_notification_creates_one_user_row_and_skips_the_global_channel()
    {
        RecordingChannel channel = new();
        (NotificationService service, TestDatabase db) = Create(channels: [channel]);
        await using (db)
        {
            NotificationSubscriptionService subscriptions = TestNotifications.Subscriptions(db);
            await subscriptions.SubscribeAsync(
                7, MovieEntity(), new NotificationSubscriptionOptions { IncludeDescendants = false });

            Notification? result = await service.PublishAsync(new NotificationMessage
            {
                Key = DownloadNotificationTopics.DownloadImported,
                Title = "Movie imported",
                Body = "The Matrix is now available",
                Entity = MovieEntity(),
                Audience = NotificationAudience.Subscribers
            });

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.UserId).IsEqualTo(7);
            await Assert.That(result.Delivered).IsTrue();
            await Assert.That(channel.Broadcast).IsEmpty();
            await Assert.That(channel.Direct.Count).IsEqualTo(1);
            await Assert.That(channel.Direct[0].UserId).IsEqualTo(7);
        }
    }

    [Test]
    public async Task Subscriber_notification_with_no_subscribers_records_nothing()
    {
        RecordingChannel channel = new();
        (NotificationService service, TestDatabase db) = Create(channels: [channel]);
        await using (db)
        {
            Notification? result = await service.PublishAsync(new NotificationMessage
            {
                Key = DownloadNotificationTopics.DownloadImported,
                Title = "Movie imported",
                Entity = MovieEntity(),
                Audience = NotificationAudience.Subscribers
            });

            await Assert.That(result).IsNull();
            await Assert.That(await service.QueryAsync(new NotificationQuery())).IsEmpty();
            await Assert.That(channel.Broadcast).IsEmpty();
            await Assert.That(channel.Direct).IsEmpty();
        }
    }

    [Test]
    public async Task Broadcast_notification_still_uses_the_global_channel()
    {
        RecordingChannel channel = new();
        (NotificationService service, TestDatabase db) = Create(channels: [channel]);
        await using (db)
        {
            Notification? result = await service.PublishAsync(Message(topic: TestNotificationTopics.Success));

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.UserId).IsNull();
            await Assert.That(channel.Broadcast.Count).IsEqualTo(1);
            await Assert.That(channel.Direct).IsEmpty();
        }
    }

    [Test]
    public async Task Publish_ExplicitToggle_EnablesDefaultDisabledTopic()
    {
        NotificationConfig config = new();
        config.Topics[TestNotificationTopics.Noisy] = true;
        (NotificationService service, TestDatabase db) = Create(config);
        await using (db)
        {
            Notification? result = await service.PublishAsync(
                Message(topic: TestNotificationTopics.Noisy, title: "Noisy"));

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.Category).IsEqualTo("test");
        }
    }
}
