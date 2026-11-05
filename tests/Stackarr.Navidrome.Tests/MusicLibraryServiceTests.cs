using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Navidrome;
using Stackarr.Navidrome.Configurations;
using Stackarr.TestSupport;

namespace Stackarr.Navidrome.Tests;

/// <summary>
/// Exercises the per-user music personalisation layer: playlist ownership filtering and the
/// per-user play history, against a real (in-memory SQLite) database.
/// </summary>
public sealed class MusicLibraryServiceTests : IDisposable
{
    readonly SqliteConnection _connection;
    readonly TestDbContextFactory _factory;
    readonly FakeHttpMessageHandler _handler = new();

    public MusicLibraryServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        DbContextOptions<StackarrDbContext> options = new DbContextOptionsBuilder<StackarrDbContext>()
            .UseSqlite(_connection)
            .Options;
        using (StackarrDbContext db = new(options, [new MusicPlaylistOwnerConfiguration(), new MusicPlayHistoryConfiguration()]))
        {
            db.Database.EnsureCreated();
        }
        _factory = new TestDbContextFactory(options);
    }

    public void Dispose() => _connection.Dispose();

    sealed class TestDbContextFactory(DbContextOptions<StackarrDbContext> options) : IDbContextFactory<StackarrDbContext>
    {
        public StackarrDbContext CreateDbContext() => new(options, [new MusicPlaylistOwnerConfiguration(), new MusicPlayHistoryConfiguration()]);
        public ValueTask<StackarrDbContext> CreateDbContextAsync(CancellationToken ct = default) => ValueTask.FromResult(CreateDbContext());
    }

    sealed class OptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    MusicLibraryService Build()
    {
        _handler.Fallback = (req, _) =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            string json = url.Contains("createPlaylist")
                ? """{ "subsonic-response": { "status": "ok", "playlist": { "id": "pl-new", "name": "New" } } }"""
                : """
                  { "subsonic-response": { "status": "ok", "playlists": { "playlist": [
                      { "id": "pl-a", "name": "A", "songCount": 1 },
                      { "id": "pl-b", "name": "B", "songCount": 2 },
                      { "id": "pl-new", "name": "New", "songCount": 0 } ] } } }
                  """;
            return Task.FromResult(FakeHttpMessageHandler.JsonResponse(json));
        };

        NavidromeConfig config = new() { Enabled = true, ApiPassword = "secret" };
        NavidromeClient client = new("http://navidrome:4533/", "stackarr", config.ApiPassword, _handler);
        NavidromeApiSecret secret = new() { Username = "stackarr", Password = config.ApiPassword, WasConfigured = true };
        NavidromeService navidrome = new(client, secret, new OptionsMonitor<NavidromeConfig>(config), NullLogger<NavidromeService>.Instance);
        return new MusicLibraryService(_factory, navidrome, NullLogger<MusicLibraryService>.Instance);
    }

    async Task SeedOwnerAsync(string playlistId, int userId)
    {
        await using StackarrDbContext db = _factory.CreateDbContext();
        db.Set<Stackarr.Navidrome.Models.MusicPlaylistOwner>().Add(new Stackarr.Navidrome.Models.MusicPlaylistOwner
        {
            PlaylistId = playlistId,
            OwnerUserId = userId,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task GetPlaylistsForUser_ShowsOwnedPlusAdminUnowned_NotOtherUsers()
    {
        MusicLibraryService service = Build();
        await SeedOwnerAsync("pl-a", userId: 1);

        List<SubsonicPlaylist> user1 = await service.GetPlaylistsForUserAsync(1, isAdmin: false);
        List<SubsonicPlaylist> user2 = await service.GetPlaylistsForUserAsync(2, isAdmin: false);
        List<SubsonicPlaylist> admin = await service.GetPlaylistsForUserAsync(3, isAdmin: true);

        await Assert.That(user1.Select(p => p.Id)).IsEquivalentTo(new[] { "pl-a" });
        await Assert.That(user2).IsEmpty();
        // Admin sees unowned/legacy playlists (pl-b, pl-new) but not another user's playlist (pl-a).
        await Assert.That(admin.Select(p => p.Id)).IsEquivalentTo(new[] { "pl-b", "pl-new" });
    }

    [Test]
    public async Task CreatePlaylist_RecordsOwner_SoOnlyThatUserSeesIt()
    {
        MusicLibraryService service = Build();

        string? id = await service.CreatePlaylistAsync(userId: 7, "New", []);

        await Assert.That(id).IsEqualTo("pl-new");
        List<SubsonicPlaylist> owner = await service.GetPlaylistsForUserAsync(7, isAdmin: false);
        List<SubsonicPlaylist> other = await service.GetPlaylistsForUserAsync(8, isAdmin: false);
        await Assert.That(owner.Select(p => p.Id)).IsEquivalentTo(new[] { "pl-new" });
        await Assert.That(other).IsEmpty();
    }

    [Test]
    public async Task DeletePlaylist_RemovesOwnershipRow()
    {
        MusicLibraryService service = Build();
        await service.CreatePlaylistAsync(userId: 7, "New", []);

        bool deleted = await service.DeletePlaylistAsync(userId: 7, isAdmin: false, "pl-new");

        await Assert.That(deleted).IsTrue();
        await Assert.That(await service.GetPlaylistsForUserAsync(7, isAdmin: false)).IsEmpty();
    }

    [Test]
    public async Task DeletePlaylist_ByNonOwner_IsRejected()
    {
        MusicLibraryService service = Build();
        await service.CreatePlaylistAsync(userId: 7, "New", []);

        bool deleted = await service.DeletePlaylistAsync(userId: 8, isAdmin: false, "pl-new");

        await Assert.That(deleted).IsFalse();
        await Assert.That(await service.GetPlaylistsForUserAsync(7, isAdmin: false)).IsNotEmpty();
    }

    [Test]
    public async Task PlayHistory_IsPerUser_Deduped_MostRecentFirst()
    {
        MusicLibraryService service = Build();
        await service.RecordPlayAsync(1, "s1", "One", "A", "Alb", "");
        await service.RecordPlayAsync(1, "s2", "Two", "A", "Alb", "");
        await service.RecordPlayAsync(1, "s1", "One", "A", "Alb", "");
        await service.RecordPlayAsync(2, "s3", "Three", "B", "Alb2", "");

        List<Stackarr.Navidrome.Models.MusicPlayHistory> user1 = await service.GetRecentlyPlayedAsync(1);
        List<Stackarr.Navidrome.Models.MusicPlayHistory> user2 = await service.GetRecentlyPlayedAsync(2);

        await Assert.That(user1.Select(h => h.SongId)).IsEquivalentTo(new[] { "s1", "s2" });
        await Assert.That(user1[0].SongId).IsEqualTo("s1"); // most recent first
        await Assert.That(user2.Select(h => h.SongId)).IsEquivalentTo(new[] { "s3" });
    }

    [Test]
    public async Task ClaimUnownedPlaylists_AdoptsLegacyPlaylistsForTheUser()
    {
        MusicLibraryService service = Build();
        await SeedOwnerAsync("pl-a", userId: 1);

        int claimed = await service.ClaimUnownedPlaylistsAsync(userId: 2);

        await Assert.That(claimed).IsEqualTo(2); // pl-b and pl-new
        List<SubsonicPlaylist> user2 = await service.GetPlaylistsForUserAsync(2, isAdmin: false);
        await Assert.That(user2.Select(p => p.Id)).IsEquivalentTo(new[] { "pl-b", "pl-new" });
    }
}
