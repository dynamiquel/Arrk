using Arrk.Notifications;

namespace Arrk.Notifications.Tests;

/// <summary>
/// Exercises per-category webhook routing: the most specific topic prefix wins, blank overrides and
/// non-matching topics fall back to the channel's default URL.
/// </summary>
public class WebhookRoutingTests
{
    static Dictionary<string, string> Routing(params (string Prefix, string Url)[] entries) =>
        entries.ToDictionary(e => e.Prefix, e => e.Url);

    [Test]
    public async Task Resolve_NoRouting_UsesDefault()
    {
        string? url = WebhookRouting.Resolve(routing: null, defaultUrl: "https://default", topic: "media.added");

        await Assert.That(url).IsEqualTo("https://default");
    }

    [Test]
    public async Task Resolve_NoRoutingNoDefault_IsNull()
    {
        string? url = WebhookRouting.Resolve(routing: null, defaultUrl: "", topic: "media.added");

        await Assert.That(url).IsNull();
    }

    [Test]
    public async Task Resolve_MatchingPrefix_UsesOverride()
    {
        Dictionary<string, string> routing = Routing(("media", "https://media"));

        string? url = WebhookRouting.Resolve(routing, "https://default", "media.added");

        await Assert.That(url).IsEqualTo("https://media");
    }

    [Test]
    public async Task Resolve_NestedPrefixes_MostSpecificWins()
    {
        Dictionary<string, string> routing = Routing(
            ("media", "https://media"),
            ("media.music", "https://music"));

        string? url = WebhookRouting.Resolve(routing, "https://default", "media.music.album.grabbed");

        await Assert.That(url).IsEqualTo("https://music");
    }

    [Test]
    public async Task Resolve_PrefixRespectsSegmentBoundary()
    {
        Dictionary<string, string> routing = Routing(("med", "https://med"));

        string? url = WebhookRouting.Resolve(routing, "https://default", "media.added");

        await Assert.That(url).IsEqualTo("https://default");
    }

    [Test]
    public async Task Resolve_BlankOverride_FallsBackToDefault()
    {
        Dictionary<string, string> routing = Routing(("media", ""));

        string? url = WebhookRouting.Resolve(routing, "https://default", "media.added");

        await Assert.That(url).IsEqualTo("https://default");
    }

    [Test]
    public async Task Resolve_NonMatchingTopic_UsesDefault()
    {
        Dictionary<string, string> routing = Routing(("system", "https://system"));

        string? url = WebhookRouting.Resolve(routing, "https://default", "media.added");

        await Assert.That(url).IsEqualTo("https://default");
    }

    [Test]
    public async Task Resolve_HashWildcard_MatchesAnyDepth()
    {
        Dictionary<string, string> routing = Routing(("downloads.#", "https://downloads"));

        await Assert.That(WebhookRouting.Resolve(routing, "https://default", "downloads"))
            .IsEqualTo("https://downloads");
        await Assert.That(WebhookRouting.Resolve(routing, "https://default", "downloads.torrent.added"))
            .IsEqualTo("https://downloads");
    }

    [Test]
    public async Task Resolve_StarWildcard_MatchesAnyDepth()
    {
        Dictionary<string, string> routing = Routing(("media.tv.*", "https://tv"));

        await Assert.That(WebhookRouting.Resolve(routing, "https://default", "media.tv.movie.grabbed"))
            .IsEqualTo("https://tv");
        await Assert.That(WebhookRouting.Resolve(routing, "https://default", "media.music.album.added"))
            .IsEqualTo("https://default");
    }

    [Test]
    public async Task Resolve_PlusWildcard_MatchesExactlyOneLevel()
    {
        Dictionary<string, string> routing = Routing(("system.health.+", "https://health"));

        await Assert.That(WebhookRouting.Resolve(routing, "https://default", "system.health.storage"))
            .IsEqualTo("https://health");
        await Assert.That(WebhookRouting.Resolve(routing, "https://default", "system.health.storage.low"))
            .IsEqualTo("https://default");
    }

    [Test]
    public async Task Resolve_WildcardAndDeeperPattern_MostSpecificWins()
    {
        Dictionary<string, string> routing = Routing(
            ("media.*", "https://media"),
            ("media.music.*", "https://music"));

        await Assert.That(WebhookRouting.Resolve(routing, "https://default", "media.tv.added"))
            .IsEqualTo("https://media");
        await Assert.That(WebhookRouting.Resolve(routing, "https://default", "media.music.album.added"))
            .IsEqualTo("https://music");
    }

    [Test]
    public async Task Resolve_BlankKey_IsIgnored()
    {
        Dictionary<string, string> routing = Routing(("", "https://blank"));

        await Assert.That(WebhookRouting.Resolve(routing, "https://default", "media.added"))
            .IsEqualTo("https://default");
    }
}
