using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Stackarr.Core;

namespace Stackarr.Music.Tests;

/// <summary>
/// The baseline migration must produce the current music schema (Deezer identity, no MusicBrainz
/// columns), so a broken migration chain or stale snapshot fails the suite instead of a live start.
/// </summary>
public class MusicSchemaMigrationTests
{
    [Test]
    public async Task Migrations_Apply_WithCurrentMusicSchema()
    {
        string path = Path.Combine(Path.GetTempPath(), $"stackarr-music-schema-{Guid.NewGuid():N}.db");
        try
        {
            DbContextOptions<StackarrDbContext> options = new DbContextOptionsBuilder<StackarrDbContext>()
                .UseSqlite($"Data Source={path}")
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;

            await using (StackarrDbContext db = new(options, []))
            {
                IReadOnlyList<string> pendingBefore = (await db.Database.GetPendingMigrationsAsync()).ToList();
                await db.Database.MigrateAsync();
                await Assert.That(pendingBefore).IsNotEmpty();
                await Assert.That(await db.Database.GetPendingMigrationsAsync()).IsEmpty();
            }

            await using SqliteConnection connection = new($"Data Source={path}");
            await connection.OpenAsync();
            foreach (string table in new[] { "MusicArtist", "MusicAlbum", "MusicTrack" })
            {
                HashSet<string> columns = await ColumnsAsync(connection, table);
                await Assert.That(columns.Contains("MusicBrainzId")).IsFalse();
            }
            // The Deezer identity index exists on the artist table after the migration.
            await Assert.That((await ColumnsAsync(connection, "MusicArtist")).Contains("DeezerId")).IsTrue();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static async Task<HashSet<string>> ColumnsAsync(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }
}
