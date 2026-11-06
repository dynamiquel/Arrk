using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Arrk.Core;
using Arrk.Requests.Configurations;

namespace Arrk.Requests.Tests;

internal sealed class TestDbContextFactory(DbContextOptions<ArrkDbContext> options)
    : IDbContextFactory<ArrkDbContext>
{
    public ArrkDbContext CreateDbContext() => new(options, Configs);

    public ValueTask<ArrkDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ArrkDbContext(options, Configs));

    static readonly IModuleEntityConfiguration[] Configs =
        [new MediaRequestConfiguration(), new RequestBlacklistConfiguration()];
}

internal static class TestDb
{
    public static string Path(string name) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stackarr-requests-{name}-{Guid.NewGuid():N}.db");

    public static TestDbContextFactory Factory(out string dbPath)
    {
        dbPath = Path("test");
        DbContextOptions<ArrkDbContext> options = new DbContextOptionsBuilder<ArrkDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using (ArrkDbContext db = new(options, [new MediaRequestConfiguration(), new RequestBlacklistConfiguration()]))
        {
            db.Database.EnsureCreated();
        }

        return new TestDbContextFactory(options);
    }
}
