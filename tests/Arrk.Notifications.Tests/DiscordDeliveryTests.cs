using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.Core.Notifications;
using Arrk.Notifications.Discord;
using Arrk.TestSupport;

namespace Arrk.Notifications.Tests;

/// <summary>
/// Exercises the real Discord channel and webhook client against real HTTP server containers.
/// The echo container logs the parsed request body, so the exact embed JSON that went on the
/// wire is asserted. Skipped when Docker is unavailable.
/// </summary>
[NotInParallel(NotificationServers.ParallelKey)]
public class DiscordDeliveryTests
{
    static NotificationMessage Message() => new()
    {
        Key = DownloadNotificationTopics.DownloadCompleted,
        Title = "Download completed",
        Body = "The Matrix finished downloading",
        Severity = EventSeverity.Success,
        Url = "https://example.com/movie/1",
        Source = "hash1",
        SourceName = "The Matrix",
        Timestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        Fields =
        [
            new("Quality", "4K"),
            new("Size", "12 GB", Inline: false)
        ]
    };

    static readonly TestDatabase Db = new();

    [After(Class)]
    public static async Task DisposeDatabase() => await Db.DisposeAsync();

    static DiscordWebhookClient Client() =>
        new(TestHttp.CreateFactory(), NullLogger<DiscordWebhookClient>.Instance);

    static DiscordNotificationChannel Channel(NotificationConfig config) =>
        TestNotifications.DiscordChannel(Db, config);

    static NotificationConfig DiscordConfig(string url, bool enabled = true, string username = "Arrk") => new()
    {
        Discord = new DiscordConfig { Enabled = enabled, WebhookUrl = url, Username = username }
    };

    [Test]
    public async Task SendAsync_ToRealServer_PostsStyledEmbed()
    {
        EchoServer? echo = await NotificationServers.GetEchoAsync();
        if (echo is null) return;

        DiscordNotificationChannel channel = Channel(DiscordConfig($"{echo.BaseUrl}/discord-hook"));
        NotificationDeliveryResult result = await channel.SendAsync(Message());

        await Assert.That(result.Success).IsTrue();

        string logs = await echo.WaitForLogAsync("discord-hook", TimeSpan.FromSeconds(20));
        await Assert.That(logs).Contains("discord-hook");
        // Catalogue style: ✅ prefix, success colour 0x2ECC71 = 3066993, author + footer.
        await Assert.That(logs).Contains("✅");
        await Assert.That(logs).Contains("3066993");
        await Assert.That(logs).Contains("test-stack");
        await Assert.That(logs).Contains("https://stack.example");
        await Assert.That(logs).Contains("author");
        await Assert.That(logs).Contains("footer");
        await Assert.That(logs).Contains("Downloads");
        await Assert.That(logs).Contains("The Matrix finished downloading");
        await Assert.That(logs).Contains("Quality");
        await Assert.That(logs).Contains("4K");
    }

    [Test]
    public async Task SendAsync_WithStyleOverrides_UsesThem()
    {
        EchoServer? echo = await NotificationServers.GetEchoAsync();
        if (echo is null) return;

        NotificationMessage message = Message() with
        {
            Style = new NotificationStyle
            {
                Emoji = "🎬",
                Color = 0x9B59B6,
                AuthorName = "Media Bot",
                ThumbnailUrl = "https://example.com/poster.jpg",
                ImageUrl = "https://example.com/backdrop.jpg",
                FooterText = "Arrk • Library"
            }
        };

        DiscordNotificationChannel channel = Channel(DiscordConfig($"{echo.BaseUrl}/styled-hook"));
        NotificationDeliveryResult result = await channel.SendAsync(message);

        await Assert.That(result.Success).IsTrue();

        string logs = await echo.WaitForLogAsync("styled-hook", TimeSpan.FromSeconds(20));
        await Assert.That(logs).Contains("🎬");
        await Assert.That(logs).Contains("10181046"); // 0x9B59B6
        await Assert.That(logs).Contains("Media Bot");
        await Assert.That(logs).Contains("poster.jpg");
        await Assert.That(logs).Contains("backdrop.jpg");
        await Assert.That(logs).Contains("thumbnail");
        await Assert.That(logs).Contains("image");
        await Assert.That(logs).Contains("Arrk • Library");
    }

    [Test]
    public async Task SendAsync_RoutingOverride_PostsToCategoryWebhook()
    {
        EchoServer? echo = await NotificationServers.GetEchoAsync();
        if (echo is null) return;

        NotificationConfig config = DiscordConfig($"{echo.BaseUrl}/default-hook");
        config.Discord.Routing["test.media"] = $"{echo.BaseUrl}/routed-media-hook";

        NotificationMessage message = Message() with
        {
            Key = TestNotificationTopics.Media,
            Title = "Routed media alert",
            Body = "Dune: Part Two was grabbed"
        };

        DiscordNotificationChannel channel = Channel(config);
        NotificationDeliveryResult result = await channel.SendAsync(message);

        await Assert.That(result.Success).IsTrue();
        // The only request that reaches this path is the routed one; a missed route would time out.
        string logs = await echo.WaitForLogAsync("routed-media-hook", TimeSpan.FromSeconds(20));
        await Assert.That(logs).Contains("routed-media-hook");
        await Assert.That(logs).Contains("Routed media alert");
    }

    [Test]
    public async Task SendAsync_RelativeArt_IsNotSentToDiscord()
    {
        EchoServer? echo = await NotificationServers.GetEchoAsync();
        if (echo is null) return;

        NotificationMessage message = Message() with
        {
            Title = "Relative art alert",
            Style = new NotificationStyle
            {
                ThumbnailUrl = "/api/music/player/cover/abc",
                ImageUrl = "/media/poster.jpg"
            }
        };

        DiscordNotificationChannel channel = Channel(DiscordConfig($"{echo.BaseUrl}/relative-hook"));
        NotificationDeliveryResult result = await channel.SendAsync(message);

        await Assert.That(result.Success).IsTrue();
        string logs = await echo.WaitForLogAsync("Relative art alert", TimeSpan.FromSeconds(20));
        await Assert.That(logs).Contains("Relative art alert");
        await Assert.That(logs).DoesNotContain("/api/music/player/cover");
        await Assert.That(logs).DoesNotContain("/media/poster.jpg");
    }

    [Test]
    public async Task SendAsync_MediaEvent_UsesMediaStyling()
    {
        EchoServer? echo = await NotificationServers.GetEchoAsync();
        if (echo is null) return;

        NotificationMessage message = Message() with
        {
            Key = TestNotificationTopics.Media,
            Title = "Movie added",
            Body = "Dune: Part Two (2024) was added",
            Severity = EventSeverity.Info
        };

        DiscordNotificationChannel channel = Channel(DiscordConfig($"{echo.BaseUrl}/media-hook"));
        NotificationDeliveryResult result = await channel.SendAsync(message);

        await Assert.That(result.Success).IsTrue();
        string logs = await echo.WaitForLogAsync("media-hook", TimeSpan.FromSeconds(20));
        await Assert.That(logs).Contains("🎬");
        await Assert.That(logs).Contains("10181046"); // media domain colour 0x9B59B6
    }

    [Test]
    public async Task SendAsync_WithCustomUsername_UsesIt()
    {
        EchoServer? echo = await NotificationServers.GetEchoAsync();
        if (echo is null) return;

        DiscordNotificationChannel channel = Channel(DiscordConfig($"{echo.BaseUrl}/custom-user", username: "MediaBot"));
        NotificationDeliveryResult result = await channel.SendAsync(Message());

        await Assert.That(result.Success).IsTrue();
        string logs = await echo.WaitForLogAsync("custom-user", TimeSpan.FromSeconds(20));
        await Assert.That(logs).Contains("MediaBot");
    }

    [Test]
    public async Task SendAsync_WithAvatar_SetsAvatarUrl()
    {
        EchoServer? echo = await NotificationServers.GetEchoAsync();
        if (echo is null) return;

        NotificationConfig config = DiscordConfig($"{echo.BaseUrl}/avatar-hook");
        config.Discord.AvatarUrl = "https://example.com/avatar.png";
        DiscordNotificationChannel channel = Channel(config);

        NotificationDeliveryResult result = await channel.SendAsync(Message());

        await Assert.That(result.Success).IsTrue();
        string logs = await echo.WaitForLogAsync("avatar-hook", TimeSpan.FromSeconds(20));
        await Assert.That(logs).Contains("avatar.png");
        await Assert.That(logs).Contains("avatar_url");
        // The avatar also brands the author line and footer icon.
        await Assert.That(logs).Contains("icon_url");
    }

    [Test]
    public async Task IsConfigured_RequiresEnabledAndUrl()
    {
        await Assert.That(Channel(DiscordConfig("http://localhost:1/hook")).IsConfigured).IsTrue();
        await Assert.That(Channel(DiscordConfig("http://localhost:1/hook", enabled: false)).IsConfigured).IsFalse();
        await Assert.That(Channel(DiscordConfig("")).IsConfigured).IsFalse();
    }

    [Test]
    public async Task SendAsync_WhenDisabled_ReturnsFailureWithoutHttp()
    {
        DiscordNotificationChannel channel = Channel(DiscordConfig("", enabled: false));

        NotificationDeliveryResult result = await channel.SendAsync(Message());

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).Contains("not configured");
    }

    [Test]
    public async Task SendAsync_ExcludedTopic_SkipsWithoutHttp()
    {
        int deadPort = Arrk.TestSupport.DockerTestContainer.GetAvailablePort();
        NotificationConfig config = DiscordConfig($"http://localhost:{deadPort}/hook");
        config.Discord.Exclude.Add("downloads.completed");

        DiscordNotificationChannel channel = Channel(config);
        NotificationDeliveryResult result = await channel.SendAsync(Message());

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Skipped).IsTrue();
    }

    [Test]
    public async Task SendAsync_ExcludeWildcard_SkipsMatchingTopicsOnly()
    {
        NotificationConfig config = DiscordConfig("http://localhost:1/hook");
        config.Discord.Exclude.Add("system.health.network.speedtest.+.completed");

        DiscordNotificationChannel channel = Channel(config);
        NotificationDeliveryResult excluded = await channel.SendAsync(Message() with
        {
            Key = "system.health.network.speedtest.vpn.completed"
        });
        NotificationDeliveryResult delivered = await channel.SendAsync(Message() with
        {
            Key = "system.health.network.speedtest.vpn.degraded"
        });

        await Assert.That(excluded.Skipped).IsTrue();
        // Not excluded: delivery is attempted (and fails because port 1 is closed).
        await Assert.That(delivered.Skipped).IsFalse();
        await Assert.That(delivered.Success).IsFalse();
    }

    [Test]
    public async Task WebhookClient_NonSuccessStatus_ReturnsFailure()
    {
        HttpBinServer? httpBin = await NotificationServers.GetHttpBinAsync();
        if (httpBin is null) return;

        DiscordWebhookClient client = Client();
        DiscordWebhookPayload payload = new() { Username = "Arrk", Embeds = [new DiscordEmbed { Title = "x" }] };

        NotificationDeliveryResult result = await client.SendAsync($"{httpBin.BaseUrl}/status/400", payload);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).Contains("400");
    }

    [Test]
    public async Task WebhookClient_RateLimited_RetriesThenFails()
    {
        HttpBinServer? httpBin = await NotificationServers.GetHttpBinAsync();
        if (httpBin is null) return;

        DiscordWebhookClient client = Client();
        DiscordWebhookPayload payload = new() { Embeds = [new DiscordEmbed { Title = "x" }] };

        NotificationDeliveryResult result = await client.SendAsync($"{httpBin.BaseUrl}/status/429", payload);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).Contains("rate limited");
    }

    [Test]
    public async Task WebhookClient_EmptyUrl_ReturnsFailure()
    {
        DiscordWebhookClient client = Client();

        NotificationDeliveryResult result = await client.SendAsync("", new DiscordWebhookPayload());

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).Contains("empty");
    }

    [Test]
    public async Task WebhookClient_UnreachableHost_ReturnsFailureNotThrow()
    {
        int deadPort = Arrk.TestSupport.DockerTestContainer.GetAvailablePort();
        DiscordWebhookClient client = Client();

        NotificationDeliveryResult result = await client.SendAsync(
            $"http://localhost:{deadPort}/hook",
            new DiscordWebhookPayload { Embeds = [new DiscordEmbed { Title = "x" }] });

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).Contains("request failed");
    }

    [Test]
    public async Task NotificationService_WithDiscordChannel_RecordsDeliveredInboxEntry()
    {
        EchoServer? echo = await NotificationServers.GetEchoAsync();
        if (echo is null) return;

        await using TestDatabase db = new();
        NotificationConfig config = DiscordConfig($"{echo.BaseUrl}/e2e-discord");
        DiscordNotificationChannel channel = Channel(config);
        NotificationService service = new(
            db.Factory,
            new MutableOptionsMonitor<NotificationConfig>(config),
            TestCatalog.Registry(),
            TestNotifications.Subscriptions(db),
            [channel],
            TimeProvider.System,
            NullLogger<NotificationService>.Instance);

        Core.Models.Notification? result = await service.PublishAsync(Message());

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Delivered).IsTrue();
        await Assert.That(result.Channels).Contains("discord");
        await Assert.That(result.Error).IsNull();
    }
}
