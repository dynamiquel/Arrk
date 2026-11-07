using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Notifications;
using Arrk.Notifications.Discord;
using Arrk.TestSupport;

namespace Arrk.Notifications.Tests;

/// <summary>Simple <see cref="IOptionsMonitor{T}"/> backed by a mutable value.</summary>
/// <summary>Builds <see cref="ArrkDbContext"/> instances against a fixed set of options.</summary>
public sealed class TestDbContextFactory(DbContextOptions<ArrkDbContext> options) : IDbContextFactory<ArrkDbContext>
{
    public ArrkDbContext CreateDbContext() => new(options, []);

    public ValueTask<ArrkDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => ValueTask.FromResult(new ArrkDbContext(options, []));
}

/// <summary>
/// A real SQLite database (shared in-memory connection) with the full Arrk schema created,
/// so services are exercised against a genuine relational store rather than an in-memory fake.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    readonly SqliteConnection _connection;

    public DbContextOptions<ArrkDbContext> Options { get; }
    public TestDbContextFactory Factory { get; }

    public TestDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        Options = new DbContextOptionsBuilder<ArrkDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (ArrkDbContext db = new(Options, []))
            db.Database.EnsureCreated();

        Factory = new TestDbContextFactory(Options);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}

/// <summary>Real <see cref="IHttpClientFactory"/> (no fakes) for channel tests.</summary>
public static class TestHttp
{
    public static IHttpClientFactory CreateFactory()
    {
        ServiceCollection services = new();
        services.AddHttpClient();
        ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IHttpClientFactory>();
    }
}

/// <summary>Builds the notification services with their real dependencies against a test database.</summary>
public static class TestNotifications
{
    public static NotificationSubscriptionService Subscriptions(TestDatabase db) =>
        new(db.Factory, TimeProvider.System, NullLogger<NotificationSubscriptionService>.Instance);

    public static UserNotificationChannelService UserChannels(TestDatabase db) =>
        new(db.Factory, TimeProvider.System);

    public static DiscordBotClient Bot(DiscordNotificationConfig config) =>
        new(TestHttp.CreateFactory(),
            new MutableOptionsMonitor<DiscordNotificationConfig>(config),
            NullLogger<DiscordBotClient>.Instance);

    public static NotificationStackInfo StackInfo(
        string name = "test-stack",
        string url = "https://stack.example") =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["StackName"] = name })
                .Build(),
            new MutableOptionsMonitor<RoutesConfig>(new RoutesConfig { PublicUrl = url }));

    public static DiscordNotificationClient DiscordChannel(TestDatabase db, DiscordNotificationConfig config) =>
        new(new DiscordWebhookClient(TestHttp.CreateFactory(), NullLogger<DiscordWebhookClient>.Instance),
            Bot(config),
            UserChannels(db),
            StackInfo(),
            EmptyScopeFactory(),
            new MutableOptionsMonitor<DiscordNotificationConfig>(config),
            TestCatalog.Registry(),
            NullLogger<DiscordNotificationClient>.Instance);

    static IServiceScopeFactory EmptyScopeFactory()
    {
        ServiceCollection services = new();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
