using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Arrk.Core;

namespace Arrk.Auth.Tests;

/// <summary>
/// A real SQLite database (temp file) with the production migrations applied (Identity,
/// OpenIddict and Arrk's own tables).
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public string Path { get; }

    public TestDatabase()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arrk-auth-{Guid.NewGuid():N}.db");

        DbContextOptions<ArrkDbContext> options = new DbContextOptionsBuilder<ArrkDbContext>()
            .UseSqlite(ConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using ArrkDbContext db = new(options, []);
        db.Database.Migrate();
    }

    public string ConnectionString => $"Data Source={Path}";

    public ArrkDbContext CreateContext()
    {
        DbContextOptions<ArrkDbContext> options = new DbContextOptionsBuilder<ArrkDbContext>()
            .UseSqlite(ConnectionString)
            .Options;
        return new ArrkDbContext(options, []);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
        catch
        {
            /* best effort */
        }

        return ValueTask.CompletedTask;
    }
}
