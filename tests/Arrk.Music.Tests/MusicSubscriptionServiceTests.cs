using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Arrk.Music.Models;
using Arrk.Music.Services;
using Arrk.Core;
using Arrk.Core.Services;
using Arrk.Music;
using Arrk.Music.Configurations;
using Arrk.Navidrome;
using Arrk.Navidrome.Configurations;
using Arrk.Navidrome.Models;
using Arrk.TestSupport;

namespace Arrk.Music.Tests;

/// <summary>
/// Exercises the playlist-subscription flow (Deezer source → Lidarr downloads → Navidrome projection)
/// against a real in-memory SQLite database and fake HTTP backends, so no downloads actually happen.
/// </summary>
public sealed class MusicSubscriptionServiceTests : IDisposable
{
    readonly SqliteConnection _connection;
    readonly TestDbContextFactory _factory;
    readonly FakeHttpMessageHandler _navidromeHandler = new();

    public MusicSubscriptionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        DbContextOptions<ArrkDbContext> options = new DbContextOptionsBuilder<ArrkDbContext>()
            .UseSqlite(_connection)
            .Options;
        using (ArrkDbContext db = new(options, Configs))
            db.Database.EnsureCreated();
        _factory = new TestDbContextFactory(options);
    }

    public void Dispose() => _connection.Dispose();

    static IModuleEntityConfiguration[] Configs =>
    [
        new MusicPlaylistOwnerConfiguration(),
        new MusicPlayHistoryConfiguration(),
        new MusicSubscriptionConfiguration(),
        new MusicArtistConfiguration(),
        new MusicAlbumConfiguration(),
        new MusicTrackConfiguration(),
        new LibraryIntentConfiguration(),
        new GrabRecordConfiguration()
    ];

    sealed class TestDbContextFactory(DbContextOptions<ArrkDbContext> options) : IDbContextFactory<ArrkDbContext>
    {
        public ArrkDbContext CreateDbContext() => new(options, Configs);
        public ValueTask<ArrkDbContext> CreateDbContextAsync(CancellationToken ct = default) => ValueTask.FromResult(CreateDbContext());
    }

    sealed class OptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    MusicSubscriptionService Build(
        Func<HttpRequestMessage, string>? navidromeSearch = null,
        Hangfire.IBackgroundJobClient? backgroundJobs = null,
        Func<HttpRequestMessage, string>? deezerResponder = null,
        LiveActivityRegistry? liveActivities = null)
    {
        // Deezer: every playlist has one track.
        FakeHttpMessageHandler deezerHandler = new();
        deezerHandler.Fallback = (req, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(
            deezerResponder?.Invoke(req) ?? DefaultDeezerPlaylist));

        // Navidrome: search3 finds the track; createPlaylist returns an id.
        _navidromeHandler.Fallback = (req, _) =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            string json = url.Contains("search3")
                ? navidromeSearch?.Invoke(req)
                    ?? """{ "subsonic-response": { "status": "ok", "searchResult3": { "song": [ { "id": "s1", "title": "Track", "artist": "Artist" } ] } } }"""
                : url.Contains("createPlaylist")
                    ? """{ "subsonic-response": { "status": "ok", "playlist": { "id": "pl-sub", "name": "Chart" } } }"""
                    : """{ "subsonic-response": { "status": "ok" } }""";
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        DeezerService deezer = new(null, deezerHandler);

        NavidromeConfig config = new() { Enabled = true, ApiPassword = "secret" };
        NavidromeClient navClient = new("http://navidrome:4533/", "arrk", "secret", _navidromeHandler);
        NavidromeApiSecret secret = new() { Username = "arrk", Password = "secret", WasConfigured = true };
        NavidromeService navidrome = new(navClient, secret, new OptionsMonitor<NavidromeConfig>(config), NullLogger<NavidromeService>.Instance);
        MusicLibraryService library = new(_factory, navidrome, NullLogger<MusicLibraryService>.Instance);

        MusicLibrary musicLibrary = new(_factory, new OptionsMonitor<NavidromeConfig>(config), navClient, NullLogger<MusicLibrary>.Instance);
        MusicAcquisitionService acquisition = MusicTestHelpers.Acquisition(
            musicLibrary, new TestMusicDb(), MusicTestHelpers.EmptyHandler(), MusicTestHelpers.EmptyHandler());
        MusicCatalogService catalog = new(deezer, musicLibrary, acquisition, NullLogger<MusicCatalogService>.Instance);

        return new MusicSubscriptionService(
            _factory, deezer, catalog, navidrome, library, liveActivities ?? new LiveActivityRegistry(),
            NullLogger<MusicSubscriptionService>.Instance, backgroundJobs);
    }

    /// <summary>Records the jobs Hangfire would enqueue without a Hangfire server.</summary>
    sealed class RecordingJobs : Hangfire.IBackgroundJobClient
    {
        public List<string> Enqueued { get; } = [];

        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state)
        {
            Enqueued.Add(job.Type.Name);
            return Guid.NewGuid().ToString();
        }

        public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) => true;
    }

    static MusicItem Playlist(long id = 3155776842) => new()
    {
        EntityType = MusicEntityType.Playlist,
        DeezerId = id,
        Title = "Top Worldwide",
        ArtUrl = "http://cdn/pl.jpg"
    };

    const string DefaultDeezerPlaylist =
        """{ "data": [ { "id": 1, "title": "Track", "artist": { "id": 9, "name": "Artist" }, "album": { "id": 2, "title": "Album", "cover_xl": "c" } } ] }""";

    static string DeezerPlaylist(int count)
    {
        string items = string.Join(",", Enumerable.Range(0, count).Select(i =>
            $$"""{ "id": {{i + 1}}, "title": "Song {{i + 1}}", "artist": { "id": 9, "name": "Artist" }, "album": { "id": 2, "title": "Album", "cover_xl": "c" } }"""));
        return $$"""{ "data": [ {{items}} ] }""";
    }

    [Test]
    public async Task Subscribe_ProjectsIntoNavidrome_AndRecordsMatch()
    {
        MusicSubscriptionService service = Build();

        MusicSubscription? sub = await service.SubscribeAsync(userId: 1, Playlist());

        await Assert.That(sub).IsNotNull();
        await Assert.That(sub!.NavidromePlaylistId).IsEqualTo("pl-sub");
        await Assert.That(sub.TrackCount).IsEqualTo(1);
        await Assert.That(sub.MatchCount).IsEqualTo(1);
        await Assert.That(_navidromeHandler.Requests.Any(r => r.Uri.AbsoluteUri.Contains("createPlaylist"))).IsTrue();

        // The full source track list is stored so the library can show every song (greyed when missing).
        List<MusicSubscriptionTrack> tracks = service.GetTracks(sub);
        await Assert.That(tracks.Count).IsEqualTo(1);
        await Assert.That(tracks[0].Title).IsEqualTo("Track");
        await Assert.That(tracks[0].InLibrary).IsTrue();
    }

    [Test]
    public async Task Subscribe_IsPerUser_AndIdempotent()
    {
        MusicSubscriptionService service = Build();

        await service.SubscribeAsync(userId: 1, Playlist(100));
        await service.SubscribeAsync(userId: 1, Playlist(100)); // duplicate — ignored

        await Assert.That((await service.GetForUserAsync(1)).Count).IsEqualTo(1);
        await Assert.That(await service.GetForUserAsync(2)).IsEmpty();
        await Assert.That(await service.IsSubscribedAsync(1, 100)).IsTrue();
        await Assert.That(await service.IsSubscribedAsync(2, 100)).IsFalse();
    }

    [Test]
    public async Task GetForUserBySource_ReturnsOnlyTheUsersSubscription()
    {
        MusicSubscriptionService service = Build();
        MusicSubscription? sub = await service.SubscribeAsync(userId: 1, Playlist(400));

        MusicSubscription? found = await service.GetForUserBySourceAsync(1, 400);
        MusicSubscription? otherUser = await service.GetForUserBySourceAsync(2, 400);
        MusicSubscription? missing = await service.GetForUserBySourceAsync(1, 999);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo(sub!.Id);
        await Assert.That(otherUser).IsNull();
        await Assert.That(missing).IsNull();
    }

    [Test]
    public async Task ExcludeSubscribed_HidesSubscriptionProjections_FromEditablePlaylists()
    {
        List<SubsonicPlaylist> playlists =
        [
            new() { Id = "pl-sub", Name = "Chart (subscribed)" },
            new() { Id = "pl-mine", Name = "My playlist" }
        ];
        List<MusicSubscription> subscriptions =
        [
            new() { Id = 1, OwnerUserId = 1, NavidromePlaylistId = "pl-sub", Name = "Chart" },
            new() { Id = 2, OwnerUserId = 1, NavidromePlaylistId = "", Name = "Not yet synced" }
        ];

        List<SubsonicPlaylist> editable = MusicSubscriptionService.ExcludeSubscribed(playlists, subscriptions);

        await Assert.That(editable.Select(p => p.Id)).IsEquivalentTo(new[] { "pl-mine" });
    }

    [Test]
    public async Task Unsubscribe_RemovesSubscription()
    {
        MusicSubscriptionService service = Build();
        MusicSubscription? sub = await service.SubscribeAsync(userId: 1, Playlist(200));

        bool removed = await service.UnsubscribeAsync(userId: 1, isAdmin: false, sub!.Id);

        await Assert.That(removed).IsTrue();
        await Assert.That(await service.GetForUserAsync(1)).IsEmpty();
    }

    [Test]
    public async Task Unsubscribe_ByAnotherUser_IsRejected()
    {
        MusicSubscriptionService service = Build();
        MusicSubscription? sub = await service.SubscribeAsync(userId: 1, Playlist(300));

        bool removed = await service.UnsubscribeAsync(userId: 2, isAdmin: false, sub!.Id);

        await Assert.That(removed).IsFalse();
        await Assert.That((await service.GetForUserAsync(1)).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Sync_QueuesMissingSongs_WithoutDuplicating()
    {
        RecordingJobs jobs = new();
        MusicSubscriptionService service = Build(
            navidromeSearch: _ => """{ "subsonic-response": { "status": "ok", "searchResult3": { "song": [] } } }""",
            backgroundJobs: jobs);

        MusicSubscription? sub = await service.SubscribeAsync(userId: 1, Playlist(500));

        using (ArrkDbContext db = _factory.CreateDbContext())
            await Assert.That(db.Set<MusicAcquireQueueItem>().Count()).IsEqualTo(1);

        // The post-import re-sync fires after every import; the queue dedupes, so no duplicate rows.
        await service.SyncAsync(sub!);

        using (ArrkDbContext db = _factory.CreateDbContext())
            await Assert.That(db.Set<MusicAcquireQueueItem>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Sync_QueuesEveryMissingSong()
    {
        RecordingJobs jobs = new();
        MusicSubscriptionService service = Build(
            navidromeSearch: _ => """{ "subsonic-response": { "status": "ok", "searchResult3": { "song": [] } } }""",
            backgroundJobs: jobs,
            deezerResponder: _ => DeezerPlaylist(31));

        await service.SubscribeAsync(userId: 1, Playlist(600));

        using ArrkDbContext db = _factory.CreateDbContext();
        await Assert.That(db.Set<MusicAcquireQueueItem>().Count()).IsEqualTo(31);
    }

    [Test]
    public async Task Sync_MatchesARemasteredDeezerTitle_ToTheLibrariesEditionTitle()
    {
        // Live loop: the playlist asks for "Invisible Touch (Remastered 2007)" while the library file
        // (and Navidrome) says "Invisible Touch (2007 Remaster)". The raw title never matched, so the
        // song stayed Missing and was queued/downloaded on every sync even though it was held.
        RecordingJobs jobs = new();
        MusicSubscriptionService service = Build(
            deezerResponder: _ => """
                { "data": [ { "id": 3145739, "title": "Invisible Touch (Remastered 2007)",
                    "artist": { "id": 1, "name": "Genesis" },
                    "album": { "id": 1347441, "title": "Invisible Touch (2007 Remaster)", "cover_xl": "c" } } ] }
                """,
            navidromeSearch: req =>
            {
                string query = Uri.UnescapeDataString(req.RequestUri!.Query);
                // Only the clean title finds the indexed edition title.
                return query.Contains("Remastered", StringComparison.OrdinalIgnoreCase)
                    ? """{ "subsonic-response": { "status": "ok", "searchResult3": { "song": [] } } }"""
                    : """{ "subsonic-response": { "status": "ok", "searchResult3": { "song": [ { "id": "song-1", "title": "Invisible Touch (2007 Remaster)", "artist": "Genesis" } ] } } }""";
            },
            backgroundJobs: jobs);

        MusicSubscription? sub = await service.SubscribeAsync(userId: 1, Playlist(800));

        await Assert.That(sub).IsNotNull();
        await Assert.That(sub!.MatchCount).IsEqualTo(1);
        List<MusicSubscriptionTrack> tracks = service.GetTracks(sub);
        await Assert.That(tracks[0].InLibrary).IsTrue();
        using ArrkDbContext db = _factory.CreateDbContext();
        await Assert.That(db.Set<MusicAcquireQueueItem>().Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Sync_PublishesALiveActivity_AndClearsItWhenDone()
    {
        LiveActivityRegistry liveActivities = new();
        List<LiveActivityEntry> seen = [];
        liveActivities.Changed += () => seen.AddRange(liveActivities.Active);

        MusicSubscriptionService service = Build(liveActivities: liveActivities);

        await service.SubscribeAsync(userId: 1, Playlist(700));

        await Assert.That(seen.Any(e => e.Kind == LiveActivityKind.Sync && e.Title.Contains("Top Worldwide"))).IsTrue();
        await Assert.That(seen.Any(e => e.Kind == LiveActivityKind.Sync && (e.Detail ?? "").Contains("Navidrome"))).IsTrue();
        await Assert.That(liveActivities.Count).IsEqualTo(0);
    }
}
