using Microsoft.EntityFrameworkCore;
using Arrk.Core;
using Arrk.Core.Configuration;

namespace Arrk.MediaLifecycle.Tests;

/// <summary>A real SQLite temp-file database, matching production's relational behaviour.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    readonly string _path;

    TestDatabase(string path, IDbContextFactory<ArrkDbContext> factory, TestTimeProvider time)
    {
        _path = path;
        Factory = factory;
        Time = time;
    }

    public IDbContextFactory<ArrkDbContext> Factory { get; }

    public TestTimeProvider Time { get; }

    public static async Task<TestDatabase> CreateAsync(DateTimeOffset? now = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"stackarr-test-{Guid.NewGuid():N}.db");
        DbContextOptions<ArrkDbContext> options = new DbContextOptionsBuilder<ArrkDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        TestContextFactory factory = new(options);
        await using ArrkDbContext db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return new TestDatabase(path, factory, new TestTimeProvider(now ?? DateTimeOffset.UtcNow));
    }

    public async ValueTask DisposeAsync()
    {
        try { File.Delete(_path); } catch { /* best effort */ }
        await Task.CompletedTask;
    }

    sealed class TestContextFactory(DbContextOptions<ArrkDbContext> options) : IDbContextFactory<ArrkDbContext>
    {
        public ArrkDbContext CreateDbContext() => new(options, [new LifecycleItemStateConfiguration()]);

        public Task<ArrkDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

public sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now = Now.Add(by);
}

internal static class TestConfig
{
    public static ArrkConfig WithStaleness(
        double upper,
        double lower,
        bool autoCleanup,
        double keep = 45,
        double keepLower = 0,
        double musicUpper = -1)
    {
        ArrkConfig config = new();
        config.Monitoring.Tv.Stale.UpperThreshold = upper;
        config.Monitoring.Tv.Stale.LowerThreshold = lower;
        config.Monitoring.Tv.Stale.AutoCleanup = autoCleanup;
        config.Monitoring.Tv.Stale.KeepThreshold = keep;
        config.Monitoring.Tv.Stale.KeepLowerThreshold = keepLower;

        config.Monitoring.Music.Stale.UpperThreshold = musicUpper >= 0 ? musicUpper : upper;
        config.Monitoring.Music.Stale.LowerThreshold = lower;
        config.Monitoring.Music.Stale.AutoCleanup = autoCleanup;
        config.Monitoring.Music.Stale.KeepThreshold = keep;
        config.Monitoring.Music.Stale.KeepLowerThreshold = keepLower;
        return config;
    }
}
