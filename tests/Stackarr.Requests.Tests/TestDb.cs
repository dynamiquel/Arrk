using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Stackarr.Core;
using Stackarr.Requests.Configurations;

namespace Stackarr.Requests.Tests;

internal sealed class TestDbContextFactory(DbContextOptions<StackarrDbContext> options)
    : IDbContextFactory<StackarrDbContext>
{
    public StackarrDbContext CreateDbContext() => new(options, Configs);

    public ValueTask<StackarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new StackarrDbContext(options, Configs));

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
        DbContextOptions<StackarrDbContext> options = new DbContextOptionsBuilder<StackarrDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using (StackarrDbContext db = new(options, [new MediaRequestConfiguration(), new RequestBlacklistConfiguration()]))
        {
            db.Database.EnsureCreated();
        }

        return new TestDbContextFactory(options);
    }
}
