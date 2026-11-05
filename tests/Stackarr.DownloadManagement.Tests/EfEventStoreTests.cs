using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stackarr.Core;
using Stackarr.Core.Events;
using Stackarr.Core.Models;
using Stackarr.Core.Notifications;
namespace Stackarr.DownloadManagement.Tests;

/// <summary>
/// Tests the real EF Core backed event store against an in-memory SQLite database
/// (full schema via EnsureCreated), so storage, filtering, and purging are exercised end to end.
/// </summary>
public class EfEventStoreTests : IDisposable
{
    readonly SqliteConnection _connection;
    readonly IDbContextFactory<StackarrDbContext> _factory;

    public EfEventStoreTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        DbContextOptions<StackarrDbContext> options = new DbContextOptionsBuilder<StackarrDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (StackarrDbContext db = new(options, []))
        {
            db.Database.EnsureCreated();
        }

        _factory = new TestDbContextFactory(options);
    }

    public void Dispose() => _connection.Dispose();

    sealed class TestDbContextFactory(DbContextOptions<StackarrDbContext> options) : IDbContextFactory<StackarrDbContext>
    {
        public StackarrDbContext CreateDbContext() => new(options, []);
        public ValueTask<StackarrDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => ValueTask.FromResult(new StackarrDbContext(options, []));
    }

    static SystemEventRecord Record(
        string type,
        string message,
        EventSeverity severity = EventSeverity.Info,
        string category = EventCategory.Download,
        DateTime? timestamp = null) => new()
        {
            Category = category,
            EventType = type,
            Message = message,
            Severity = severity,
            Source = "hash1",
            SourceName = "Torrent One",
            Timestamp = timestamp ?? DateTime.UtcNow
        };

    [Test]
    public async Task RecordAsync_PersistsEvent()
    {
        EfEventStore store = new(_factory);

        await store.RecordAsync(Record(DownloadEventType.Added, "added"));

        IReadOnlyList<SystemEvent> events = await store.QueryAsync(new SystemEventQuery());
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo(DownloadEventType.Added);
        await Assert.That(events[0].Category).IsEqualTo(EventCategory.Download);
        await Assert.That(events[0].Severity).IsEqualTo("Info");
        await Assert.That(events[0].SourceName).IsEqualTo("Torrent One");
    }

    [Test]
    public async Task RecordAsync_PersistsNotificationTopic()
    {
        EfEventStore store = new(_factory);

        await store.RecordAsync(Record(DownloadEventType.Completed, "done") with
        {
            Notification = new NotificationMessage
            {
                Key = DownloadNotificationTopics.DownloadCompleted,
                Title = "Download completed"
            }
        });
        await store.RecordAsync(Record(DownloadEventType.Progress, "halfway"));

        IReadOnlyList<SystemEvent> events = await store.QueryAsync(new SystemEventQuery());
        SystemEvent notified = events.Single(e => e.EventType == DownloadEventType.Completed);
        SystemEvent auditOnly = events.Single(e => e.EventType == DownloadEventType.Progress);

        await Assert.That(notified.Topic).IsEqualTo(DownloadNotificationTopics.DownloadCompleted);
        await Assert.That(auditOnly.Topic).IsNull();
    }

    [Test]
    public async Task RecordAsync_Batch_PersistsAll()
    {
        EfEventStore store = new(_factory);

        await store.RecordAsync([
            Record(DownloadEventType.Added, "a"),
            Record(DownloadEventType.Progress, "p"),
            Record(DownloadEventType.Completed, "c", EventSeverity.Success)
        ]);

        await Assert.That((await store.QueryAsync(new SystemEventQuery())).Count).IsEqualTo(3);
    }

    [Test]
    public async Task QueryAsync_FiltersByCategory()
    {
        EfEventStore store = new(_factory);
        await store.RecordAsync(Record("A", "download"));
        await store.RecordAsync(Record("B", "health", category: EventCategory.Health));

        IReadOnlyList<SystemEvent> events = await store.QueryAsync(new SystemEventQuery { Category = EventCategory.Health });

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo("B");
    }

    [Test]
    public async Task QueryAsync_FiltersByEventType()
    {
        EfEventStore store = new(_factory);
        await store.RecordAsync(Record(DownloadEventType.Added, "a"));
        await store.RecordAsync(Record(DownloadEventType.Completed, "c"));

        IReadOnlyList<SystemEvent> events = await store.QueryAsync(new SystemEventQuery { EventType = DownloadEventType.Completed });

        await Assert.That(events.Count).IsEqualTo(1);
    }

    [Test]
    public async Task QueryAsync_FiltersByMinSeverity()
    {
        EfEventStore store = new(_factory);
        await store.RecordAsync(Record("A", "info", EventSeverity.Info));
        await store.RecordAsync(Record("B", "warn", EventSeverity.Warning));
        await store.RecordAsync(Record("C", "err", EventSeverity.Error));

        IReadOnlyList<SystemEvent> events = await store.QueryAsync(new SystemEventQuery { MinSeverity = EventSeverity.Warning });

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events.Select(e => e.Severity)).DoesNotContain("Info");
    }

    [Test]
    public async Task QueryAsync_FiltersBySearchAcrossMessageAndType()
    {
        EfEventStore store = new(_factory);
        await store.RecordAsync(Record(DownloadEventType.Added, "Torrent added: Ubuntu"));
        await store.RecordAsync(Record(DownloadEventType.Completed, "Torrent completed: Fedora"));

        IReadOnlyList<SystemEvent> byMessage = await store.QueryAsync(new SystemEventQuery { Search = "Ubuntu" });
        IReadOnlyList<SystemEvent> byType = await store.QueryAsync(new SystemEventQuery { Search = DownloadEventType.Completed });

        await Assert.That(byMessage.Count).IsEqualTo(1);
        await Assert.That(byMessage[0].Message).Contains("Ubuntu");
        await Assert.That(byType.Count).IsEqualTo(1);
        await Assert.That(byType[0].EventType).IsEqualTo(DownloadEventType.Completed);
    }

    [Test]
    public async Task QueryAsync_FiltersBySearchAcrossNotificationTopic()
    {
        EfEventStore store = new(_factory);
        await store.RecordAsync(Record(DownloadEventType.Completed, "done") with
        {
            Topic = DownloadNotificationTopics.DownloadCompleted
        });
        await store.RecordAsync(Record(DownloadEventType.Progress, "halfway"));

        IReadOnlyList<SystemEvent> byTopic = await store.QueryAsync(new SystemEventQuery
        {
            Search = DownloadNotificationTopics.DownloadCompleted
        });
        IReadOnlyList<SystemEvent> byTopicPrefix = await store.QueryAsync(new SystemEventQuery
        {
            Search = "downloads."
        });

        await Assert.That(byTopic.Count).IsEqualTo(1);
        await Assert.That(byTopic[0].Topic).IsEqualTo(DownloadNotificationTopics.DownloadCompleted);
        await Assert.That(byTopicPrefix.Count).IsEqualTo(1);
    }

    [Test]
    public async Task QueryAsync_OrdersNewestFirst_AndRespectsLimit()
    {
        EfEventStore store = new(_factory);
        DateTime t0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await store.RecordAsync(Record("A", "oldest", timestamp: t0));
        await store.RecordAsync(Record("B", "middle", timestamp: t0.AddMinutes(1)));
        await store.RecordAsync(Record("C", "newest", timestamp: t0.AddMinutes(2)));

        IReadOnlyList<SystemEvent> events = await store.QueryAsync(new SystemEventQuery { Limit = 2 });

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events[0].EventType).IsEqualTo("C");
        await Assert.That(events[1].EventType).IsEqualTo("B");
    }

    [Test]
    public async Task QueryAsync_FiltersBySince()
    {
        EfEventStore store = new(_factory);
        DateTime t0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await store.RecordAsync(Record("Old", "old", timestamp: t0));
        await store.RecordAsync(Record("New", "new", timestamp: t0.AddHours(1)));

        IReadOnlyList<SystemEvent> events = await store.QueryAsync(new SystemEventQuery { Since = t0.AddMinutes(30) });

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0].EventType).IsEqualTo("New");
    }

    [Test]
    public async Task PurgeOlderThanAsync_RemovesOnlyOldEvents()
    {
        EfEventStore store = new(_factory);
        DateTime t0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await store.RecordAsync(Record("Old", "old", timestamp: t0));
        await store.RecordAsync(Record("New", "new", timestamp: t0.AddDays(1)));

        int removed = await store.PurgeOlderThanAsync(t0.AddHours(1));

        await Assert.That(removed).IsEqualTo(1);
        IReadOnlyList<SystemEvent> remaining = await store.QueryAsync(new SystemEventQuery());
        await Assert.That(remaining.Count).IsEqualTo(1);
        await Assert.That(remaining[0].EventType).IsEqualTo("New");
    }

    [Test]
    public async Task GetEventTypesAsync_ReturnsDistinctTypes()
    {
        EfEventStore store = new(_factory);
        await store.RecordAsync(Record(DownloadEventType.Added, "a"));
        await store.RecordAsync(Record(DownloadEventType.Added, "a2"));
        await store.RecordAsync(Record(DownloadEventType.Completed, "c"));

        IReadOnlyList<string> types = await store.GetEventTypesAsync(EventCategory.Download);

        await Assert.That(types).IsEquivalentTo([DownloadEventType.Added, DownloadEventType.Completed]);
    }

    [Test]
    public async Task QueryAsync_EmptyStore_ReturnsEmpty()
    {
        EfEventStore store = new(_factory);

        await Assert.That(await store.QueryAsync(new SystemEventQuery())).IsEmpty();
    }

    [Test]
    public async Task RecordAsync_ConcurrentWrites_AllPersisted()
    {
        EfEventStore store = new(_factory);

        await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(i => store.RecordAsync(Record(DownloadEventType.Progress, $"p{i}"))));

        IReadOnlyList<SystemEvent> events = await store.QueryAsync(new SystemEventQuery { Limit = 100 });
        await Assert.That(events.Count).IsEqualTo(25);
    }
}
