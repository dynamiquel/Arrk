using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Stackarr.Core;

namespace Stackarr.Jellyfin.Tests;

/// <summary>
/// A real SQLite database with the production migrations, used by the account-sync and SSO
/// installer tests so the Stackarr side of the flow is exercised exactly as in the app.
/// </summary>
public sealed class AccountTestDatabase : IAsyncDisposable
{
    public string Path { get; }
    public IDbContextFactory<StackarrDbContext> Factory { get; }

    public AccountTestDatabase()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stackarr-jf-auth-{Guid.NewGuid():N}.db");

        DbContextOptions<StackarrDbContext> options = new DbContextOptionsBuilder<StackarrDbContext>()
            .UseSqlite(ConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using (StackarrDbContext db = new(options, []))
        {
            db.Database.Migrate();
        }

        Factory = new AccountDbContextFactory(options);
    }

    public string ConnectionString => $"Data Source={Path}";

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

internal sealed class AccountDbContextFactory(DbContextOptions<StackarrDbContext> options) : IDbContextFactory<StackarrDbContext>
{
    public StackarrDbContext CreateDbContext() => new(options, []);

    public ValueTask<StackarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new StackarrDbContext(options, []));
}
