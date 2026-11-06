using Hangfire.Storage.SQLite;
using Hangfire.Storage.SQLite.Entities;
using SQLite;
using Arrk.App.Services;

namespace Arrk.App.Tests;

/// <summary>
/// Pins the Hangfire storage contract: the job database lives under <c>config/</c> and its connections
/// are opened full-mutex. sqlite-net's default (<see cref="SQLiteOpenFlags.NoMutex"/>) lets the
/// distributed-lock heartbeat race the owning job thread on one connection, which corrupts the indexes.
/// </summary>
public class HangfireStorageTests
{
    [Test]
    public async Task DatabasePath_IsUnderConfigDirectory()
    {
        await Assert.That(HangfireStorage.DatabasePath("/data"))
            .IsEqualTo(Path.Combine("/data", "config", "Hangfire.db"));
    }

    [Test]
    public async Task ConnectionString_SerialisesTheSharedConnection()
    {
        SQLiteConnectionString connectionString = HangfireStorage.ConnectionString("/data/config/Hangfire.db");

        await Assert.That(connectionString.OpenFlags.HasFlag(SQLiteOpenFlags.FullMutex)).IsTrue();
        await Assert.That(connectionString.OpenFlags.HasFlag(SQLiteOpenFlags.NoMutex)).IsFalse();
    }

    [Test]
    public async Task Heartbeat_WhileWriting_KeepsTheIndexesIntact()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"stackarr-hangfire-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "Hangfire.db");

        try
        {
            // A short lock lifetime makes the heartbeat fire every 80 ms while the "job" thread keeps
            // writing through the same connection, which is the upstream corruption scenario.
            SQLiteStorageOptions options = new() { DistributedLockLifetime = TimeSpan.FromMilliseconds(400) };
            using SQLiteStorage storage = new(HangfireStorage.CreateConnectionFactory(databasePath), options);
            using HangfireDbContext context = storage.CreateAndOpenConnection();

            using (SQLiteDistributedLock.Acquire("HangFire:test", TimeSpan.FromSeconds(5), context, options))
            {
                for (int i = 0; i < 400; i++)
                {
                    context.Database.Insert(new JobParameter
                    {
                        JobId = i,
                        Name = "test",
                        Value = "value",
                        ExpireAt = DateTime.UtcNow.AddMinutes(5),
                    });
                    await Task.Delay(2);
                }
            }

            using SQLiteConnection check = new(databasePath);
            List<string> integrity = check.QueryScalars<string>("PRAGMA integrity_check");

            await Assert.That(integrity).IsEquivalentTo(["ok"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
