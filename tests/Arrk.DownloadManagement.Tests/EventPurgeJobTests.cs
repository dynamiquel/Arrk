using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.Core.Models;
using Arrk.TestSupport;

namespace Arrk.DownloadManagement.Tests;

/// <summary>
/// Covers the retention job's configuration mapping: a default age for the whole log, a shorter
/// age for noisy topics, and 0/null meaning "keep forever".
/// </summary>
public class EventPurgeJobTests : IDisposable
{
    readonly SqliteConnection _connection;
    readonly IDbContextFactory<ArrkDbContext> _factory;

    public EventPurgeJobTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        DbContextOptions<ArrkDbContext> options = new DbContextOptionsBuilder<ArrkDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (ArrkDbContext db = new(options, []))
        {
            db.Database.EnsureCreated();
        }

        _factory = new TestDbContextFactory(options);
    }

    public void Dispose() => _connection.Dispose();

    sealed class TestDbContextFactory(DbContextOptions<ArrkDbContext> options) : IDbContextFactory<ArrkDbContext>
    {
        public ArrkDbContext CreateDbContext() => new(options, []);
        public ValueTask<ArrkDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => ValueTask.FromResult(new ArrkDbContext(options, []));
    }

    static SystemEventRecord Record(
        string type,
        DateTime timestamp,
        string? topic = null,
        string category = EventCategory.Download) => new()
        {
            Category = category,
            EventType = type,
            Message = type,
            Topic = topic,
            Timestamp = timestamp
        };

    static MutableOptionsMonitor<ArrkConfig> Config(EventRetentionConfig retention) =>
        new(new ArrkConfig { Events = new EventsConfig { Retention = retention } });

    [Test]
    public async Task Defaults_KeepJobEventsForSevenDays()
    {
        EventRetentionConfig config = new();

        await Assert.That(config.Enabled).IsTrue();
        await Assert.That(config.DefaultRetentionDays).IsEqualTo(30);
        await Assert.That(config.TopicRetentionDays["job.#"]).IsEqualTo(7);
    }

    [Test]
    public async Task Execute_PurgesByDefaultAndTopicRetention()
    {
        DateTime now = new(2026, 11, 7, 4, 30, 0, DateTimeKind.Utc);
        TestTimeProvider time = new(now);
        EfEventStore store = new(_factory);
        await store.RecordAsync([
            Record("job-old", now.AddDays(-10), "job.metrics.started", EventCategory.Job),
            Record("job-new", now.AddDays(-5), "job.metrics.started", EventCategory.Job),
            Record("download-old", now.AddDays(-40), "downloads.completed"),
            Record("download-new", now.AddDays(-10), "downloads.completed"),
            Record("audit-old", now.AddDays(-40))
        ]);

        EventPurgeJob job = new(
            store,
            Config(new EventRetentionConfig
            {
                Enabled = true,
                DefaultRetentionDays = 30,
                TopicRetentionDays = new() { ["job.#"] = 7 }
            }),
            time,
            NullLogger<EventPurgeJob>.Instance);

        await job.Execute(CancellationToken.None);

        IReadOnlyList<SystemEvent> remaining = await store.QueryAsync(new SystemEventQuery { Limit = 100 });
        await Assert.That(remaining.Select(e => e.EventType)).IsEquivalentTo(["job-new", "download-new"]);
    }

    [Test]
    public async Task Execute_DoesNothingWhenDisabled()
    {
        DateTime now = new(2026, 11, 7, 4, 30, 0, DateTimeKind.Utc);
        TestTimeProvider time = new(now);
        EfEventStore store = new(_factory);
        await store.RecordAsync(Record("old", now.AddDays(-400)));

        EventPurgeJob job = new(
            store,
            Config(new EventRetentionConfig { Enabled = false }),
            time,
            NullLogger<EventPurgeJob>.Instance);

        await job.Execute(CancellationToken.None);

        await Assert.That((await store.QueryAsync(new SystemEventQuery())).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Execute_ZeroRetentionKeepsForever()
    {
        DateTime now = new(2026, 11, 7, 4, 30, 0, DateTimeKind.Utc);
        TestTimeProvider time = new(now);
        EfEventStore store = new(_factory);
        await store.RecordAsync(Record("old", now.AddDays(-400)));

        EventPurgeJob job = new(
            store,
            Config(new EventRetentionConfig
            {
                Enabled = true,
                DefaultRetentionDays = 0,
                TopicRetentionDays = new() { ["job.#"] = 0 }
            }),
            time,
            NullLogger<EventPurgeJob>.Instance);

        await job.Execute(CancellationToken.None);

        await Assert.That((await store.QueryAsync(new SystemEventQuery())).Count).IsEqualTo(1);
    }
}
