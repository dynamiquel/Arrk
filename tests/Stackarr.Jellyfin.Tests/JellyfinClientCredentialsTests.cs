using Stackarr.TestSupport;

namespace Stackarr.Jellyfin.Tests;

/// <summary>
/// Verifies that <see cref="JellyfinClient"/> resolves its administrator credentials per authentication,
/// so editing <c>services.jellyfin.admin_username</c>/<c>admin_password</c> in the config editor
/// applies without an app restart.
/// </summary>
public class JellyfinClientCredentialsTests
{
    [Test]
    public async Task AuthenticateAsync_ResolvesCredentialsPerCall_SoConfigEditsApply()
    {
        FakeHttpMessageHandler handler = new();
        string username = "admin";
        string password = "first";
        JellyfinClient client = new("http://test/jellyfin", () => (username, password), "dev", handler);

        handler.EnqueueJson("""{"AccessToken":"t1","User":{"Id":"1","Name":"admin"}}""");
        string first = await client.AuthenticateAsync();

        await Assert.That(first).IsEqualTo("t1");
        await Assert.That(handler.Requests[0].Body).Contains("admin");
        await Assert.That(handler.Requests[0].Body).Contains("first");

        // Changing the admin username invalidates the cached token and re-authenticates as the new account.
        username = "boss";
        password = "second";
        handler.EnqueueJson("""{"AccessToken":"t2","User":{"Id":"2","Name":"boss"}}""");
        string second = await client.AuthenticateAsync();

        await Assert.That(second).IsEqualTo("t2");
        await Assert.That(handler.Requests.Count).IsEqualTo(2);
        await Assert.That(handler.Requests[1].Body).Contains("boss");
        await Assert.That(handler.Requests[1].Body).Contains("second");
    }

    [Test]
    public async Task BaseAddress_FollowsProxyToggle()
    {
        FakeHttpMessageHandler handler = new();
        bool proxy = true;
        JellyfinClient client = new(
            () => $"http://jellyfin:8096{(proxy ? "/jellyfin" : "")}",
            () => ("admin", "pw"),
            "dev",
            handler);

        await Assert.That(client.BaseAddress).IsEqualTo("http://jellyfin:8096/jellyfin");

        proxy = false;
        await Assert.That(client.BaseAddress).IsEqualTo("http://jellyfin:8096");
    }

    [Test]
    public async Task AuthenticateAsync_CachesToken_ForTheSameUsername()
    {
        FakeHttpMessageHandler handler = new();
        string username = "admin";
        JellyfinClient client = new("http://test/jellyfin", () => (username, "pw"), "dev", handler);

        handler.EnqueueJson("""{"AccessToken":"t1","User":{"Id":"1","Name":"admin"}}""");
        await client.AuthenticateAsync();
        string cached = await client.AuthenticateAsync();

        await Assert.That(cached).IsEqualTo("t1");
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }
}
