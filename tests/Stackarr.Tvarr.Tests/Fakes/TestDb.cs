using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Stackarr.Ai.Data;
using Stackarr.Core;
using Stackarr.Tvarr.Configurations;

namespace Stackarr.Tvarr.Tests;

internal sealed class TestDbContextFactory(DbContextOptions<StackarrDbContext> options)
    : IDbContextFactory<StackarrDbContext>
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

    public StackarrDbContext CreateDbContext() => Create();

    public ValueTask<StackarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Create());

    StackarrDbContext Create()
    {
        StackarrDbContext db = new(options, ModuleConfigurations);
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
    public static DbContextOptions<StackarrDbContext> Options(string? dbPath = null)
    {
        dbPath ??= Path.Combine(Path.GetTempPath(), $"stackarr-tvarr-test-{Guid.NewGuid():N}.db");
        return new DbContextOptionsBuilder<StackarrDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
    }
}
