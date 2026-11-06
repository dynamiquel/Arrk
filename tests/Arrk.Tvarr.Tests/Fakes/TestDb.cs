using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Arrk.Ai.Data;
using Arrk.Core;
using Arrk.Tvarr.Configurations;

namespace Arrk.Tvarr.Tests;

internal sealed class TestDbContextFactory(DbContextOptions<ArrkDbContext> options)
    : IDbContextFactory<ArrkDbContext>
{
    public static readonly IModuleEntityConfiguration[] ModuleConfigurations =
    [
        new PendingTorrentCleanupConfiguration(),
        new TvUploadRecordConfiguration(),
        new AiUsageRecordConfiguration(),
        new AiCacheEntryConfiguration(),
        new AiUserPreferenceConfiguration(),
        new AiDecisionOutcomeConfiguration()
    ];

    readonly Lock _gate = new();
    bool _created;

    public ArrkDbContext CreateDbContext() => Create();

    public ValueTask<ArrkDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Create());

    ArrkDbContext Create()
    {
        ArrkDbContext db = new(options, ModuleConfigurations);
        lock (_gate)
        {
            if (!_created)
            {
                db.Database.EnsureCreated();
                _created = true;
            }
        }

        return db;
    }
}

public static class TestDb
{
    public static IReadOnlyList<IModuleEntityConfiguration> Configurations => TestDbContextFactory.ModuleConfigurations;

    /// <summary>
    /// Creates a fresh SQLite-backed factory. The database is created in the system temp
    /// directory and cleaned up when the returned factory is disposed.
    /// </summary>
    public static DbContextOptions<ArrkDbContext> Options(string? dbPath = null)
    {
        dbPath ??= Path.Combine(Path.GetTempPath(), $"stackarr-tvarr-test-{Guid.NewGuid():N}.db");
        return new DbContextOptionsBuilder<ArrkDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
    }
}
