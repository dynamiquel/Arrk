using Arrk.Notifications;

namespace Arrk.Notifications.Tests;

public class WebhookRoutingTests
{
    [Test]
    public async Task Resolve_All_matching_routes_are_returned_in_order_without_deduplication()
    {
        List<NotificationRoute> routing =
        [
            new() { Url = "https://default" },
            new() { Topic = "media", Url = "https://media" },
            new() { Topic = "media.music.*", Url = "https://music" },
            new() { Topic = "media.music.*", Url = "https://other-music" },
            new() { Topic = "media.music.*", Url = "https://music" },
            new() { Topic = "med", Url = "https://wrong-boundary" },
            new() { Topic = "system.#", Url = "https://wrong-topic" },
            new() { Topic = "media.#", Url = " " },
            new() { Topic = "", Url = "https://blank-topic" }
        ];

        IReadOnlyList<string> urls = WebhookRouting.Resolve(routing, NotificationRecipient.Everyone, "media.music.album.added");

        await Assert.That(string.Join(",", urls)).IsEqualTo(
            "https://default,https://media,https://music,https://other-music,https://music");
    }

    [Test]
    [Arguments("downloads.#", "downloads", true)]
    [Arguments("downloads.#", "downloads.torrent.added", true)]
    [Arguments("media.tv.*", "media.tv.movie.grabbed", true)]
    [Arguments("media.tv.*", "media.music.album.added", false)]
    [Arguments("system.health.+", "system.health.storage", true)]
    [Arguments("system.health.+", "system.health.storage.low", false)]
    [Arguments("med", "media.added", false)]
    public async Task Resolve_Uses_topic_patterns(string pattern, string topic, bool matches)
    {
        IReadOnlyList<string> urls = WebhookRouting.Resolve(
            [new() { Topic = pattern, Url = "https://route" }], NotificationRecipient.Everyone, topic);

        await Assert.That(urls.Count).IsEqualTo(matches ? 1 : 0);
    }

    [Test]
    [Arguments(null, "User", "https://public")]
    [Arguments(null, "aDmIn", "https://staff")]
    [Arguments(null, "Curator", "https://staff")]
    [Arguments(null, "Unknown", "")]
    [Arguments(null, null, "")]
    [Arguments(7, "Admin", "")]
    [Arguments(7, "User", "")]
    public async Task Resolve_Roles_are_case_insensitive_and_private_recipients_never_match(
        int? userId, string? role, string expected)
    {
        List<NotificationRoute> routing =
        [
            new() { Url = "https://public" },
            new() { Url = "https://staff", FromRoles = ["Admin", "Curator"] }
        ];

        await Assert.That(string.Join(",", WebhookRouting.Resolve(routing, new(userId, role), "media.added")))
            .IsEqualTo(expected);
    }

    [Test]
    public async Task Resolve_No_routes_returns_empty()
    {
        await Assert.That(WebhookRouting.Resolve([], NotificationRecipient.Everyone, "media.added")).IsEmpty();
    }
}
