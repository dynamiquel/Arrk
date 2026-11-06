using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Arrk.Core;

namespace Arrk.Jellyfin.Tests;

/// <summary>
/// A real SQLite database with the production migrations, used by the account-sync and SSO
/// installer tests so the Arrk side of the flow is exercised exactly as in the app.
/// </summary>
public sealed class AccountTestDatabase : IAsyncDisposable
{
    public string Path { get; }
    public IDbContextFactory<ArrkDbContext> Factory { get; }

    public AccountTestDatabase()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arrk-jf-auth-{Guid.NewGuid():N}.db");

        DbContextOptions<ArrkDbContext> options = new DbContextOptionsBuilder<ArrkDbContext>()
            .UseSqlite(ConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using (ArrkDbContext db = new(options, []))
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

internal sealed class AccountDbContextFactory(DbContextOptions<ArrkDbContext> options) : IDbContextFactory<ArrkDbContext>
{
    public ArrkDbContext CreateDbContext() => new(options, []);

    public ValueTask<ArrkDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ArrkDbContext(options, []));
}
