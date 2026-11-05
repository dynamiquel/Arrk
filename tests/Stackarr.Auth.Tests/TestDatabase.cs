using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Stackarr.Core;

namespace Stackarr.Auth.Tests;

/// <summary>
/// A real SQLite database (temp file) with the production migrations applied (Identity,
/// OpenIddict and Stackarr's own tables).
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public string Path { get; }

    public TestDatabase()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stackarr-auth-{Guid.NewGuid():N}.db");

        DbContextOptions<StackarrDbContext> options = new DbContextOptionsBuilder<StackarrDbContext>()
            .UseSqlite(ConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using StackarrDbContext db = new(options, []);
        db.Database.Migrate();
    }

    public string ConnectionString => $"Data Source={Path}";

    public StackarrDbContext CreateContext()
    {
        DbContextOptions<StackarrDbContext> options = new DbContextOptionsBuilder<StackarrDbContext>()
            .UseSqlite(ConnectionString)
            .Options;
        return new StackarrDbContext(options, []);
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
