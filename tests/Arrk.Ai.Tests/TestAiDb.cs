using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Arrk.Ai.Data;
using Arrk.Core;

namespace Arrk.Ai.Tests;

/// <summary>Real SQLite (in-memory, one shared connection) for the AI entities.</summary>
public sealed class TestAiDb : IDbContextFactory<ArrkDbContext>, IDisposable
{
    static readonly IModuleEntityConfiguration[] Configs =
    [
        new AiUsageRecordConfiguration(),
        new AiCacheEntryConfiguration(),
        new AiDecisionOutcomeConfiguration(),
        new AiUserPreferenceConfiguration()
    ];

    readonly SqliteConnection _connection;
    readonly DbContextOptions<ArrkDbContext> _options;

    public TestAiDb()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ArrkDbContext>().UseSqlite(_connection).Options;
        using ArrkDbContext db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public ArrkDbContext CreateDbContext() => new(_options, Configs);

    public Task<ArrkDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    public void Dispose() => _connection.Dispose();
}
