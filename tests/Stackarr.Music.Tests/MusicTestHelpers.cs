using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Ai.Data;
using Stackarr.Core.Services;
using Stackarr.Music.Configurations;
using Stackarr.Music.Services;
using Stackarr.Navidrome;
using Stackarr.Navidrome.Configurations;
using Stackarr.Prowlarr;
using Stackarr.Prowlarr.Configuration;
using Stackarr.Slskd;
using Stackarr.TestSupport;
using Stackarr.Transmission;

namespace Stackarr.Music.Tests;

/// <summary>Builds the real music services over an in-memory SQLite library and fake HTTP transports.</summary>
static class MusicTestHelpers
{
    public static MusicAcquisitionService Acquisition(
        MusicLibrary library,
        TestMusicDb db,
        FakeHttpMessageHandler prowlarrHandler,
        FakeHttpMessageHandler transmissionHandler,
        FakeHttpMessageHandler? slskdHandler = null,
        bool slskdEnabled = false,
        IConfiguration? configuration = null,
        Hangfire.IBackgroundJobClient? backgroundJobs = null,
        FakeHttpMessageHandler? deezerHandler = null,
        SlskdConfig? slskdConfig = null,
        MusicNotifications? notifications = null,
        LiveActivityRegistry? liveActivities = null) =>
        new(
            new ProwlarrService(
                new ProwlarrClient("http://prowlarr:9696/prowlarr", "stackarr", prowlarrHandler),
                new StaticOptionsMonitor<ProwlarrConfig>(new ProwlarrConfig()),
                new StackarrInstance(new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["StackName"] = "test" }).Build()),
                NullLogger<ProwlarrService>.Instance),
            library,
            new TransmissionClient("http://transmission:9091", "u", "p", transmissionHandler),
            new SlskdService(
                new SlskdClient("http://slskd:5030", "u", "p", slskdHandler ?? EmptyHandler()),
                new StaticOptionsMonitor<SlskdConfig>(slskdConfig ?? new SlskdConfig { Enabled = slskdEnabled }),
                new ServiceRegistry(),
                NullLogger<SlskdService>.Instance),
            new DeezerService(NullLogger<DeezerService>.Instance, deezerHandler ?? EmptyHandler()),
            db,
            configuration ?? new ConfigurationBuilder().Build(),
            NullLogger<MusicAcquisitionService>.Instance,
            liveActivities ?? new LiveActivityRegistry(),
            notifications,
            backgroundJobs);

    public static MusicLibrary Library(TestMusicDb db, bool navidromeEnabled = false) =>
        new(db,
            new StaticOptionsMonitor<NavidromeConfig>(new NavidromeConfig { Enabled = navidromeEnabled }),
            new NavidromeClient("http://navidrome:4533/", "u", "p", EmptyHandler()),
            NullLogger<MusicLibrary>.Instance);

    public static SlskdService Slskd(FakeHttpMessageHandler? handler = null, bool enabled = true) =>
        new(
            new SlskdClient("http://slskd:5030", "u", "p", handler ?? EmptyHandler()),
            new StaticOptionsMonitor<SlskdConfig>(new SlskdConfig { Enabled = enabled }),
            new ServiceRegistry(),
            NullLogger<SlskdService>.Instance);

    public static FakeHttpMessageHandler EmptyHandler(string json = "[]")
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        return handler;
    }

    public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

/// <summary>Real SQLite (in-memory, one shared connection) backing a <see cref="MusicLibrary"/>.</summary>
public sealed class TestMusicDb : IDbContextFactory<StackarrDbContext>, IDisposable
{
    static readonly IModuleEntityConfiguration[] Configs =
    [
        new MusicArtistConfiguration(),
        new MusicAlbumConfiguration(),
        new MusicTrackConfiguration(),
        new LibraryIntentConfiguration(),
        new GrabRecordConfiguration(),
        new MusicAcquireQueueConfiguration(),
        new MusicPlaylistOwnerConfiguration(),
        new MusicPlayHistoryConfiguration(),
        new MusicSubscriptionConfiguration(),
        new AiUsageRecordConfiguration(),
        new AiCacheEntryConfiguration(),
        new AiDecisionOutcomeConfiguration()
    ];

    readonly SqliteConnection _connection;
    readonly DbContextOptions<StackarrDbContext> _options;

    public TestMusicDb()
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

    public void Seed(Action<StackarrDbContext> seed)
    {
        using StackarrDbContext db = CreateDbContext();
        seed(db);
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();
}
