using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.Auth.Abstractions;
using Arrk.Notifications;
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
        IEnumerable<INotificationClient>? channels = null,
        TestUserDirectory? users = null)
    {
        TestDatabase db = new();
        NotificationSubscriptionService subscriptions = TestNotifications.Subscriptions(db);
        NotificationService service = new(
            db.Factory,
            new MutableOptionsMonitor<NotificationConfig>(config ?? new NotificationConfig()),
            TestCatalog.Registry(),
            new NotificationAudienceResolver((users ?? new TestUserDirectory()).ScopeFactory(), subscriptions),
            subscriptions,
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

            await Assert.That(await service.UnreadCountAsync(7)).IsEqualTo(2);

            Notification? read = await service.MarkReadAsync(first.Id, 7);
            await Assert.That(read!.ReadAt).IsNotNull();
            await Assert.That(await service.UnreadCountAsync(7)).IsEqualTo(1);
        }
    }

    [Test]
    public async Task MarkRead_MissingId_ReturnsNull()
    {
        (NotificationService service, TestDatabase db) = Create();
        await using (db)
        {
            await Assert.That(await service.MarkReadAsync(9999, 7)).IsNull();
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

            int updated = await service.MarkAllReadAsync(7);

            await Assert.That(updated).IsEqualTo(2);
            await Assert.That(await service.UnreadCountAsync(7)).IsEqualTo(0);
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
            await service.MarkReadAsync(first.Id, 7);

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

    sealed class SkippingChannel : INotificationClient
    {
        public string Name => "skipping";

        public Task<NotificationDeliveryResult> SendAsync(
            NotificationMessage message, NotificationRecipient recipient, CancellationToken ct = default) =>
            Task.FromResult(NotificationDeliveryResult.Skip());
    }

    sealed class RecordingChannel : INotificationClient
    {
        public string Name => "recording";

        public NotificationDeliveryResult DirectResult { get; init; } = NotificationDeliveryResult.Ok();
        public NotificationDeliveryResult GlobalResult { get; init; } = NotificationDeliveryResult.Ok();
        public NotificationDeliveryResult RoleResult { get; init; } = NotificationDeliveryResult.Ok();
        public Func<Task>? DuringSend { get; set; }
        public List<NotificationMessage> Broadcast { get; } = [];
        public List<string> Roles { get; } = [];
        public List<(int UserId, NotificationMessage Message)> Direct { get; } = [];

        public async Task<NotificationDeliveryResult> SendAsync(
            NotificationMessage message, NotificationRecipient recipient, CancellationToken ct = default)
        {
            if (DuringSend is not null)
                await DuringSend();
            if (recipient.IsGlobal)
            {
                Broadcast.Add(message);
                return GlobalResult;
            }
            if (recipient.Role is { } role)
            {
                Roles.Add(role);
                return RoleResult;
            }
            Direct.Add((recipient.UserId!.Value, message));
            return DirectResult;
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
        TestUserDirectory users = new() { Accounts = [new() { Id = 7 }, new() { Id = 8 }, new() { Id = 9 }] };
        (NotificationService service, TestDatabase db) = Create(channels: [channel], users: users);
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
                Audience = new() { Global = true, FollowersOf = MovieEntity() }
            });

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.UserId).IsEqualTo(7);
            await Assert.That(result.Delivered).IsTrue();
            await Assert.That(channel.Broadcast.Count).IsEqualTo(1);
            await Assert.That(channel.Direct.Count).IsEqualTo(hasFollowers ? 1 : 0);
            IReadOnlyList<Notification> inbox = await service.QueryAsync(new NotificationQuery());
            await Assert.That(inbox.Count).IsEqualTo(3);
            Notification follower = inbox.Single(n => n.UserId == 7);
            await Assert.That(follower.Delivered).IsTrue();
            if (hasFollowers && directFails)
                await Assert.That(follower.Error).Contains("DMs are blocked");
            else
                await Assert.That(follower.Error).IsNull();
            await Assert.That(inbox.Where(n => n.UserId != 7).All(n => n.Delivered && n.Error is null)).IsTrue();
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
                Audience = new() { Global = true, FollowersOf = MovieEntity() }
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
                Audience = new() { FollowersOf = MovieEntity() }
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
                Audience = new() { FollowersOf = MovieEntity() }
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
            await Assert.That(result!.UserId).IsEqualTo(7);
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

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Audiences_union_global_roles_users_and_followers_without_duplicate_direct_delivery(bool global)
    {
        TestUserDirectory users = new()
        {
            Accounts = [new() { Id = 7 }, new() { Id = 8 }, new() { Id = 9 },
                new() { Id = 10, IsActive = false }, new() { Id = 11, Role = UserRole.Pending }]
        };
        users.Roles["Admin"] = [7, 10];
        users.Roles["Curator"] = [7, 8];
        RecordingChannel client = new() { RoleResult = NotificationDeliveryResult.Fail("role endpoint failed") };
        (NotificationService service, TestDatabase db) = Create(channels: [client], users: users);
        await using (db)
        {
            await TestNotifications.Subscriptions(db).SubscribeAsync(8, MovieEntity(), new());
            await service.PublishAsync(Message(DownloadNotificationTopics.DownloadImported) with
            {
                Audience = new() { Global = global, Roles = ["Admin", "admin", "Curator"], UserIds = [7, 10, 11, 999], FollowersOf = MovieEntity() }
            });

            await Assert.That(client.Broadcast.Count).IsEqualTo(global ? 1 : 0);
            await Assert.That(client.Roles).IsEquivalentTo(new[] { "Admin", "Curator" });
            await Assert.That(client.Direct.Select(d => d.UserId).ToArray()).IsEquivalentTo(new[] { 7, 8 });
            IReadOnlyList<Notification> inbox = await service.QueryAsync(new());
            await Assert.That(inbox.Select(n => n.UserId).ToArray())
                .IsEquivalentTo(global ? new int?[] { 7, 8, 9 } : new int?[] { 7, 8 });
            await Assert.That(inbox.Where(n => n.UserId is 7 or 8).All(n => n.Delivered && n.Error!.Contains("role endpoint failed"))).IsTrue();
            await Assert.That(await service.UnreadCountAsync(9)).IsEqualTo(global ? 1 : 0);
            if (global)
                await Assert.That(inbox.Single(n => n.UserId == 9).Error).IsNull();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Role_endpoint_outcomes_only_apply_to_members_not_unrelated_direct_recipients(bool fails)
    {
        TestUserDirectory users = new() { Accounts = [new() { Id = 7 }, new() { Id = 8 }] };
        users.Roles["Admin"] = [7];
        RecordingChannel client = new()
        {
            RoleResult = fails ? NotificationDeliveryResult.Fail("role endpoint failed") : NotificationDeliveryResult.Ok(),
            DirectResult = NotificationDeliveryResult.Skip()
        };
        (NotificationService service, TestDatabase db) = Create(channels: [client], users: users);
        await using (db)
        {
            await service.PublishAsync(Message() with { Audience = new() { Roles = ["Admin"], UserIds = [8] } });
            Notification admin = (await service.QueryAsync(new() { UserId = 7 })).Single();
            Notification other = (await service.QueryAsync(new() { UserId = 8 })).Single();
            await Assert.That(admin.Delivered).IsEqualTo(!fails);
            if (fails)
                await Assert.That(admin.Error).Contains("role endpoint failed");
            else
                await Assert.That(admin.Error).IsNull();
            await Assert.That(other.Delivered).IsFalse();
            await Assert.That(other.Error).IsNull();
            await Assert.That(client.Broadcast).IsEmpty();
            await Assert.That(client.Roles).IsEquivalentTo(new[] { "Admin" });
            await Assert.That(client.Direct.Select(d => d.UserId).ToArray()).IsEquivalentTo(new[] { 7, 8 });
        }
    }

    [Test]
    public async Task Role_endpoint_delivery_does_not_depend_on_local_members()
    {
        RecordingChannel client = new();
        (NotificationService service, TestDatabase db) = Create(channels: [client], users: new() { Accounts = [] });
        await using (db)
        {
            await service.PublishAsync(Message() with { Audience = new() { Roles = ["Admin", "admin"] } });
            await Assert.That(client.Roles).IsEquivalentTo(new[] { "Admin" });
            await Assert.That(client.Broadcast).IsEmpty();
            await Assert.That(client.Direct).IsEmpty();
            await Assert.That(await service.QueryAsync(new())).IsEmpty();
        }
    }

    [Test]
    public async Task Global_inbox_read_state_is_independent_and_requires_ownership()
    {
        TestUserDirectory users = new() { Accounts = [new() { Id = 7 }, new() { Id = 8 }] };
        (NotificationService service, TestDatabase db) = Create(users: users);
        await using (db)
        {
            await service.PublishAsync(Message());
            await service.PublishToUserAsync(7, Message());
            Notification other = (await service.QueryAsync(new() { UserId = 8 })).Single();
            await Assert.That(await service.MarkReadAsync(other.Id, 7)).IsNull();
            await Assert.That(await service.MarkAllReadAsync(7)).IsEqualTo(2);
            await Assert.That(await service.UnreadCountAsync(7)).IsEqualTo(0);
            await Assert.That(await service.UnreadCountAsync(8)).IsEqualTo(1);
            await Assert.That(await service.MarkReadAsync(other.Id, 8)).IsNotNull();
        }
    }

    [Test]
    public async Task Global_delivery_does_not_need_local_accounts_and_empty_audience_does_not_deliver()
    {
        RecordingChannel client = new();
        (NotificationService service, TestDatabase db) = Create(channels: [client], users: new() { Accounts = [] });
        await using (db)
        {
            await service.PublishAsync(Message());
            await service.PublishAsync(Message() with { Audience = new() });
            await Assert.That(client.Broadcast.Count).IsEqualTo(1);
            await Assert.That(client.Direct).IsEmpty();
            await Assert.That(await service.QueryAsync(new())).IsEmpty();
        }
    }

    [Test]
    public async Task Failing_clients_do_not_block_other_clients_or_direct_destinations()
    {
        RecordingChannel client = new() { GlobalResult = NotificationDeliveryResult.Fail("server unavailable") };
        TestUserDirectory users = new() { Accounts = [new() { Id = 7 }, new() { Id = 8 }] };
        (NotificationService service, TestDatabase db) = Create(channels: [new ThrowingClient(), client], users: users);
        await using (db)
        {
            await service.PublishAsync(Message() with { Audience = new() { Global = true, UserIds = [7, 7] } });
            Notification entry = (await service.QueryAsync(new() { UserId = 7 })).Single();
            Notification other = (await service.QueryAsync(new() { UserId = 8 })).Single();
            await Assert.That(client.Broadcast.Count).IsEqualTo(1);
            await Assert.That(client.Direct.Count).IsEqualTo(1);
            await Assert.That(entry.Delivered).IsTrue();
            await Assert.That(entry.Channels).IsEqualTo("recording");
            await Assert.That(entry.Error).Contains("server unavailable");
            await Assert.That(entry.Error).Contains("broken client");
            await Assert.That(other.Delivered).IsFalse();
            await Assert.That(other.Error).Contains("server unavailable");
        }
    }

    [Test]
    public async Task Reading_an_inbox_entry_during_delivery_is_not_overwritten_by_delivery_status()
    {
        RecordingChannel client = new();
        (NotificationService service, TestDatabase db) = Create(channels: [client]);
        await using (db)
        {
            client.DuringSend = async () =>
            {
                Notification entry = (await service.QueryAsync(new() { UserId = 7 })).Single();
                await service.MarkReadAsync(entry.Id, 7);
            };
            await service.PublishAsync(Message());
            Notification stored = (await service.QueryAsync(new() { UserId = 7 })).Single();
            await Assert.That(stored.ReadAt).IsNotNull();
            await Assert.That(stored.Delivered).IsTrue();
            await Assert.That(await service.UnreadCountAsync(7)).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancellation_propagates_but_preserves_completed_client_and_destination_outcomes(bool duringDirect)
    {
        using CancellationTokenSource cancellation = new();
        RecordingChannel client = new();
        TestUserDirectory users = new() { Accounts = [new() { Id = 7 }, new() { Id = 8 }] };
        (NotificationService service, TestDatabase db) = Create(
            channels: [new ThrowingClient(), client, new CancellingClient(cancellation, duringDirect)], users: users);
        await using (db)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.PublishAsync(
                Message() with { Audience = new() { Global = true, UserIds = [7] } }, cancellation.Token));

            IReadOnlyList<Notification> inbox = await service.QueryAsync(new());
            await Assert.That(inbox.Count).IsEqualTo(2);
            await Assert.That(inbox.All(n => n.Delivered && n.Channels == "recording" && n.Error!.Contains("broken client"))).IsTrue();
            await Assert.That(client.Broadcast.Count).IsEqualTo(1);
            await Assert.That(client.Direct.Count).IsEqualTo(duringDirect ? 1 : 0);
        }
    }

    sealed class CancellingClient(CancellationTokenSource cancellation, bool duringDirect) : INotificationClient
    {
        public string Name => "cancelling";

        public Task<NotificationDeliveryResult> SendAsync(NotificationMessage message, NotificationRecipient recipient,
            CancellationToken ct = default)
        {
            if (duringDirect && recipient.IsGlobal)
                return Task.FromResult(NotificationDeliveryResult.Skip());
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NotificationDeliveryResult.Skip());
        }
    }

    sealed class ThrowingClient : INotificationClient
    {
        public string Name => "throwing";
        public Task<NotificationDeliveryResult> SendAsync(NotificationMessage message, NotificationRecipient recipient,
            CancellationToken ct = default) => throw new InvalidOperationException("broken client");
    }
}
