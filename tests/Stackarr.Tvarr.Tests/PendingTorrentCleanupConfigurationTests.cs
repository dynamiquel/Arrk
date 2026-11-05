using Microsoft.EntityFrameworkCore;
using Stackarr.Core;
using Stackarr.Core.Events;
using Stackarr.Tvarr.Configurations;
using Stackarr.Tvarr.Models;

namespace Stackarr.Tvarr.Tests;

public class PendingTorrentCleanupConfigurationTests
{
    static StackarrDbContext CreateContext(DbContextOptions<StackarrDbContext> options) =>
        new(options, TestDb.Configurations);

    [Test]
    public async Task DuplicateHashAndService_IsRejectedByUniqueIndex()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"stackarr-cleanup-{Guid.NewGuid():N}.db");
        DbContextOptions<StackarrDbContext> options = new DbContextOptionsBuilder<StackarrDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        try
        {
            await using StackarrDbContext db = CreateContext(options);
            await db.Database.EnsureCreatedAsync();

            db.Set<PendingTorrentCleanup>().Add(new()
            {
                TorrentHash = "abc",
                TorrentName = "n",
                Reason = TorrentDeleteReason.Error,
                TargetService = "Sonarr",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            db.Set<PendingTorrentCleanup>().Add(new()
            {
                TorrentHash = "abc",
                TorrentName = "n2",
                Reason = TorrentDeleteReason.Error,
                TargetService = "Sonarr",
                CreatedAt = DateTime.UtcNow
            });

            await Assert.That(async () => await db.SaveChangesAsync())
                .Throws<DbUpdateException>();
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SameHashDifferentService_IsAllowed()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"stackarr-cleanup-{Guid.NewGuid():N}.db");
        DbContextOptions<StackarrDbContext> options = new DbContextOptionsBuilder<StackarrDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        try
        {
            await using StackarrDbContext db = CreateContext(options);
            await db.Database.EnsureCreatedAsync();

            db.Set<PendingTorrentCleanup>().Add(new()
            {
                TorrentHash = "abc", TorrentName = "n", TargetService = "Sonarr", CreatedAt = DateTime.UtcNow
            });
            db.Set<PendingTorrentCleanup>().Add(new()
            {
                TorrentHash = "abc", TorrentName = "n", TargetService = "Radarr", CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            await Assert.That(await db.Set<PendingTorrentCleanup>().CountAsync()).IsEqualTo(2);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
