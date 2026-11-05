using Hangfire.Storage.SQLite;
using SQLite;
using Stackarr.Core.Models;

namespace Stackarr.App.Services;

/// <summary>
/// Builds Hangfire's SQLite storage.
/// Hangfire.Storage.SQLite's default factory opens connections with <see cref="SQLiteOpenFlags.NoMutex"/>,
/// yet its distributed-lock heartbeat keeps writing through the owning connection from a timer thread.
/// SQLite has to serialise that shared connection or concurrent b-tree writes corrupt the indexes, so
/// Stackarr opens it full-mutex itself and keeps the job database beside the app database under
/// <c>config/</c>.
/// </summary>
public static class HangfireStorage
{
    /// <summary>
    /// Full-mutex on purpose: the distributed-lock heartbeat and the job thread share one connection.
    /// </summary>
    public const SQLiteOpenFlags OpenFlags =
        SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex;

    /// <summary>Absolute path to the Hangfire job database under the given data root.</summary>
    public static string DatabasePath(string dataRoot) => StackarrFiles.HangfireDatabasePath(dataRoot);

    /// <summary>Connection string used for the Hangfire job database.</summary>
    public static SQLiteConnectionString ConnectionString(string databasePath) =>
        new(databasePath, OpenFlags, storeDateTimeAsTicks: true);

    /// <summary>Connection factory handed to Hangfire's SQLite storage.</summary>
    public static SQLiteDbConnectionFactory CreateConnectionFactory(string databasePath) =>
        new(() => new SQLiteConnection(ConnectionString(databasePath))
        {
            BusyTimeout = TimeSpan.FromSeconds(10),
        });
}
