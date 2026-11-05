using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stackarr.Ai.Data;
using Stackarr.Core;

namespace Stackarr.Ai.Tests;

/// <summary>Real SQLite (in-memory, one shared connection) for the AI entities.</summary>
public sealed class TestAiDb : IDbContextFactory<StackarrDbContext>, IDisposable
{
    static readonly IModuleEntityConfiguration[] Configs =
    [
        new AiUsageRecordConfiguration(),
        new AiCacheEntryConfiguration(),
        new AiDecisionOutcomeConfiguration(),
        new AiUserPreferenceConfiguration()
    ];

    readonly SqliteConnection _connection;
    readonly DbContextOptions<StackarrDbContext> _options;

    public TestAiDb()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<StackarrDbContext>().UseSqlite(_connection).Options;
        using StackarrDbContext db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public StackarrDbContext CreateDbContext() => new(_options, Configs);

    public Task<StackarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    public void Dispose() => _connection.Dispose();
}
