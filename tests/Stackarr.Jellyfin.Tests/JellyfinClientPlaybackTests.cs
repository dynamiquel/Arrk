using System.Net;
using Stackarr.Jellyfin.Playback;
using Stackarr.TestSupport;

namespace Stackarr.Jellyfin.Tests;

/// <summary>
/// Exercises the playback methods on <see cref="JellyfinClient"/> against a fake HTTP handler so
/// the wire contract (URLs, methods) is pinned without a running server.
/// </summary>
public class JellyfinClientPlaybackTests
{
    static JellyfinClient Client(FakeHttpMessageHandler handler)
    {
        JellyfinClient client = new("http://test/jellyfin", "admin", "admin", "dev", handler);
        client.SetToken("token");
        return client;
    }

    [Test]
    public async Task GetPlaybackInfoAsync_PostsToItemsPlaybackInfo()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{ "PlaySessionId": "ps1", "MediaSources": [ { "Id": "ms1", "TranscodingUrl": "/videos/abc/master.m3u8?ApiKey=secret" } ] }""");
        JellyfinClient client = Client(handler);

        JellyfinPlaybackInfoResponse response = await client.GetPlaybackInfoAsync(
            "abc",
            new JellyfinPlaybackInfoRequest { UserId = "u1", DeviceProfile = new JellyfinDeviceProfile { Name = "p" } });

        await Assert.That(handler.Requests[0].Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.Requests[0].Uri.AbsolutePath).IsEqualTo("/jellyfin/Items/abc/PlaybackInfo");
        await Assert.That(handler.Requests[0].Body).Contains("deviceProfile");
        await Assert.That(response.PlaySessionId).IsEqualTo("ps1");
        await Assert.That(response.MediaSources[0].TranscodingUrl).Contains("master.m3u8");
    }

    [Test]
    public async Task SetPlayedAsync_UsesUserScopedPlayedItemsEndpoint()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueStatus(HttpStatusCode.OK);
        JellyfinClient client = Client(handler);

        await client.SetPlayedAsync("user123", "item456", played: true);

        await Assert.That(handler.Requests[0].Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.Requests[0].Uri.AbsolutePath).IsEqualTo("/jellyfin/Users/user123/PlayedItems/item456");
    }

    [Test]
    public async Task SetPlayedAsync_UnplayedUsesDelete()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueStatus(HttpStatusCode.OK);
        JellyfinClient client = Client(handler);

        await client.SetPlayedAsync("user123", "item456", played: false);

        await Assert.That(handler.Requests[0].Method).IsEqualTo(HttpMethod.Delete);
    }

    [Test]
    public async Task GetUserItemAsync_ReadsPlaybackPosition()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{ "Id": "item1", "UserData": { "Played": false, "PlaybackPositionTicks": 500000000 } }""");
        JellyfinClient client = Client(handler);

        JellyfinItem? item = await client.GetUserItemAsync("user1", "item1");

        await Assert.That(item).IsNotNull();
        await Assert.That(item!.UserData!.PlaybackPositionTicks).IsEqualTo(500000000L);
        await Assert.That(handler.Requests[0].Uri.AbsolutePath).IsEqualTo("/jellyfin/Users/user1/Items/item1");
    }

    [Test]
    public async Task SetUserPositionAsync_PostsPositionToUserScopedUserData()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueStatus(HttpStatusCode.OK);
        JellyfinClient client = Client(handler);

        await client.SetUserPositionAsync("user1", "item1", 7800000000);

        await Assert.That(handler.Requests[0].Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(handler.Requests[0].Uri.AbsolutePath).IsEqualTo("/jellyfin/Users/user1/Items/item1/UserData");
        await Assert.That(handler.Requests[0].Body).Contains("7800000000");
    }
}
