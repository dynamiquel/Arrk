using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Arrk.Navidrome;
using Arrk.TestSupport;

namespace Arrk.Navidrome.Tests;

public class NavidromeClientTests
{
    static (NavidromeClient Client, FakeHttpMessageHandler Handler) Build()
    {
        FakeHttpMessageHandler handler = new();
        return (new NavidromeClient("http://navidrome:4533/navidrome/", "arrk", "secret", handler), handler);
    }

    [Test]
    public async Task GetAlbumList_ParsesAlbums_AndSendsSubsonicAuth()
    {
        (NavidromeClient client, FakeHttpMessageHandler handler) = Build();
        handler.EnqueueJson("""
        { "subsonic-response": { "status": "ok", "version": "1.16.1",
            "albumList2": { "album": [
                { "id": "al-1", "name": "In Rainbows", "artist": "Radiohead", "artistId": "ar-1",
                  "coverArt": "al-1", "songCount": 10, "duration": 2500, "year": 2007 } ] } } }
        """);

        List<SubsonicAlbum> albums = await client.GetAlbumListAsync("newest", 30);

        await Assert.That(albums.Single().Name).IsEqualTo("In Rainbows");
        await Assert.That(albums.Single().SongCount).IsEqualTo(10);

        CapturedRequest request = handler.Requests.Single();
        await Assert.That(request.Uri.AbsolutePath).IsEqualTo("/navidrome/rest/getAlbumList2");
        await Assert.That(request.Uri.Query).Contains("u=arrk");
        await Assert.That(request.Uri.Query).Contains("v=1.16.1");
        await Assert.That(request.Uri.Query).Contains("c=Arrk");
        await Assert.That(request.Uri.Query).Contains("f=json");
        await Assert.That(request.Uri.Query).Contains("type=newest");
        await Assert.That(request.Uri.Query).Contains("t=");
        await Assert.That(request.Uri.Query).Contains("s=");
    }

    [Test]
    public async Task BaseUrl_IsResolvedPerRequest_SoProxyToggleApplies()
    {
        FakeHttpMessageHandler handler = new();
        string url = "http://navidrome:4533/navidrome/";
        NavidromeClient client = new(() => url, "arrk", "secret", handler);
        handler.EnqueueJson("""{ "subsonic-response": { "status": "ok", "version": "1.16.1", "albumList2": { "album": [] } } }""");

        await client.GetAlbumListAsync("newest", 1);

        url = "http://navidrome:4533/";
        handler.EnqueueJson("""{ "subsonic-response": { "status": "ok", "version": "1.16.1", "albumList2": { "album": [] } } }""");
        await client.GetAlbumListAsync("newest", 1);

        await Assert.That(handler.Requests[0].Uri.AbsolutePath).IsEqualTo("/navidrome/rest/getAlbumList2");
        await Assert.That(handler.Requests[1].Uri.AbsolutePath).IsEqualTo("/rest/getAlbumList2");
    }

    [Test]
    public async Task GetPlaylists_ParsesList()
    {
        (NavidromeClient client, FakeHttpMessageHandler handler) = Build();
        handler.EnqueueJson("""
        { "subsonic-response": { "status": "ok",
            "playlists": { "playlist": [ { "id": "pl-1", "name": "Favourites", "songCount": 12 } ] } } }
        """);

        List<SubsonicPlaylist> playlists = await client.GetPlaylistsAsync();

        await Assert.That(playlists.Single().Name).IsEqualTo("Favourites");
        await Assert.That(playlists.Single().SongCount).IsEqualTo(12);
    }

    [Test]
    public async Task GetAlbum_WithSongs_ParsesTracklist()
    {
        (NavidromeClient client, FakeHttpMessageHandler handler) = Build();
        handler.EnqueueJson("""
        { "subsonic-response": { "status": "ok",
            "album": { "id": "al-1", "name": "In Rainbows", "artist": "Radiohead",
                "song": [ { "id": "s-1", "title": "15 Step", "artist": "Radiohead", "duration": 237, "track": 1 } ] } } }
        """);

        SubsonicAlbumWithSongs? album = await client.GetAlbumAsync("al-1");

        await Assert.That(album).IsNotNull();
        await Assert.That(album!.Song.Single().Title).IsEqualTo("15 Step");
    }

    [Test]
    public async Task Token_IsMd5OfPasswordPlusSalt()
    {
        (NavidromeClient client, FakeHttpMessageHandler handler) = Build();
        handler.EnqueueJson("""{ "subsonic-response": { "status": "ok" } }""");

        await client.GetPlaylistsAsync();

        string query = handler.Requests.Single().Uri.Query;
        string salt = Extract(query, "s");
        string token = Extract(query, "t");
        string expected = Convert.ToHexStringLower(
            System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("secret" + salt)));
        await Assert.That(token).IsEqualTo(expected);
    }

    static string Extract(string query, string key)
    {
        foreach (string pair in query.TrimStart('?').Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == key)
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return "";
    }
}

public class NavidromeServiceTests
{
    sealed class OptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    static NavidromeService Build(NavidromeConfig config, FakeHttpMessageHandler handler)
    {
        NavidromeClient client = new("http://navidrome:4533/", "arrk", config.ApiPassword, handler);
        NavidromeApiSecret secret = new() { Username = "arrk", Password = config.ApiPassword, WasConfigured = true };
        return new NavidromeService(client, secret, new OptionsMonitor<NavidromeConfig>(config), NullLogger<NavidromeService>.Instance);
    }

    [Test]
    public async Task NotConfigured_ReturnsEmptyWithoutHttp()
    {
        FakeHttpMessageHandler handler = new();
        NavidromeService service = Build(new NavidromeConfig { Enabled = true, ApiPassword = "" }, handler);

        await Assert.That(service.IsConfigured).IsFalse();
        await Assert.That(await service.GetRecentlyAddedAsync()).IsEmpty();
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task Configured_ReturnsAlbums()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""
        { "subsonic-response": { "status": "ok", "albumList2": { "album": [ { "id": "al-1", "name": "X" } ] } } }
        """);
        NavidromeService service = Build(new NavidromeConfig { Enabled = true, ApiPassword = "secret" }, handler);

        List<SubsonicAlbum> albums = await service.GetRecentlyAddedAsync();

        await Assert.That(albums.Single().Id).IsEqualTo("al-1");
    }

    [Test]
    public async Task PlayerStreamUrl_PointsAtProxy()
    {
        await Assert.That(NavidromeService.PlayerStreamUrl("s-1")).IsEqualTo("/api/music/player/stream/s-1");
    }
}
