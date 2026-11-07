using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Auth.Abstractions;
using Arrk.Notifications;
using Arrk.Notifications.Discord;
using Arrk.TestSupport;

namespace Arrk.Notifications.Tests;

/// <summary>
/// Exercises the REST-only Discord DM path with a deterministic handler. No gateway or SDK is
/// involved: one request opens the DM channel, the next posts the embed.
/// </summary>
public class DiscordDirectMessageTests
{
    static DiscordNotificationConfig Config(bool dmEnabled = true) => new()
    {
        Enabled = true,
        DirectMessages = new DiscordDirectMessageConfig
        {
            Enabled = dmEnabled,
            BotToken = "bot-token",
            ApplicationId = "1176189027259531344",
            ApiBaseUrl = "https://discord.test/api/v10"
        }
    };

    static (DiscordBotClient Client, FakeHttpMessageHandler Handler) CreateBot(
        DiscordNotificationConfig? config = null)
    {
        FakeHttpMessageHandler handler = new();
        DiscordBotClient client = new(
            new FakeHttpClientFactory(handler),
            new MutableOptionsMonitor<DiscordNotificationConfig>(config ?? Config()),
            NullLogger<DiscordBotClient>.Instance);
        return (client, handler);
    }

    static DiscordMessagePayload Payload() => new()
    {
        Embeds =
        [
            new DiscordEmbed
            {
                Title = "Movie imported",
                Description = "The Matrix is now available",
                Color = 0x2ECC71
            }
        ]
    };

    [Test]
    public async Task Send_opens_a_dm_channel_then_posts_the_embed()
    {
        (DiscordBotClient client, FakeHttpMessageHandler handler) = CreateBot();
        handler.EnqueueJson("""{"id":"dm-1"}""");
        handler.EnqueueJson("""{"id":"message-1"}""");

        DiscordDirectMessageResult result = await client.SendDirectMessageAsync("42", Payload(), null);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.ChannelId).IsEqualTo("dm-1");
        await Assert.That(handler.Requests.Count).IsEqualTo(2);
        await Assert.That(handler.Requests[0].Uri.AbsolutePath).EndsWith("/users/@me/channels");
        await Assert.That(handler.Requests[0].Body).Contains("\"recipient_id\":\"42\"");
        await Assert.That(handler.Requests[0].Headers!["Authorization"]).IsEqualTo("Bot bot-token");
        await Assert.That(handler.Requests[1].Uri.AbsolutePath).EndsWith("/channels/dm-1/messages");
        await Assert.That(handler.Requests[1].Body).Contains("\"title\":\"Movie imported\"");
    }

    [Test]
    public async Task Cached_channel_skips_opening_a_new_dm()
    {
        (DiscordBotClient client, FakeHttpMessageHandler handler) = CreateBot();
        handler.EnqueueJson("""{"id":"message-1"}""");

        DiscordDirectMessageResult result = await client.SendDirectMessageAsync("42", Payload(), "dm-cached");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        await Assert.That(handler.Requests[0].Uri.AbsolutePath).EndsWith("/channels/dm-cached/messages");
    }

    [Test]
    public async Task Forbidden_dm_explains_the_shared_server_requirement()
    {
        (DiscordBotClient client, FakeHttpMessageHandler handler) = CreateBot();
        handler.EnqueueStatus(
            HttpStatusCode.Forbidden,
            json: """{"code":50007,"message":"Cannot send messages to this user"}""");

        DiscordDirectMessageResult result = await client.SendDirectMessageAsync("42", Payload(), null);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).Contains("share a Discord server");
    }

    [Test]
    public async Task Invalid_bot_token_is_reported_clearly()
    {
        (DiscordBotClient client, FakeHttpMessageHandler handler) = CreateBot();
        handler.EnqueueStatus(HttpStatusCode.Unauthorized, json: """{"message":"401: Unauthorized"}""");

        DiscordDirectMessageResult result = await client.SendDirectMessageAsync("42", Payload(), null);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).Contains("bot token");
    }

    [Test]
    public async Task Rate_limit_is_retried_with_discords_delay()
    {
        (DiscordBotClient client, FakeHttpMessageHandler handler) = CreateBot();
        handler.EnqueueStatus(
            HttpStatusCode.TooManyRequests,
            json: """{"retry_after":0.01,"global":false}""");
        handler.EnqueueJson("""{"id":"dm-1"}""");
        handler.EnqueueJson("""{"id":"message-1"}""");

        DiscordDirectMessageResult result = await client.SendDirectMessageAsync("42", Payload(), null);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(handler.Requests.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Invite_url_uses_the_application_id()
    {
        (DiscordBotClient client, _) = CreateBot();

        await Assert.That(client.InviteUrl()).Contains("client_id=1176189027259531344");
        await Assert.That(client.InviteUrl()).Contains("scope=bot");
    }

    [Test]
    public async Task Bot_uses_reloaded_transport_options()
    {
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"id":"dm-1"}""");
        handler.EnqueueJson("""{"id":"message-1"}""");
        MutableOptionsMonitor<DiscordNotificationConfig> options = new(Config(dmEnabled: false));
        DiscordBotClient client = new(new FakeHttpClientFactory(handler), options,
            NullLogger<DiscordBotClient>.Instance);

        await Assert.That(client.IsConfigured).IsFalse();
        DiscordNotificationConfig updated = Config();
        updated.DirectMessages.BotToken = "replacement-token";
        updated.DirectMessages.ApiBaseUrl = "https://replacement.test/api/v10";
        options.Update(updated);

        DiscordDirectMessageResult result = await client.SendDirectMessageAsync("42", Payload(), null);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(handler.Requests[0].Uri.Host).IsEqualTo("replacement.test");
        await Assert.That(handler.Requests[0].Headers!["Authorization"]).IsEqualTo("Bot replacement-token");
    }

    [Test]
    public async Task Client_skips_direct_and_global_delivery_when_disabled_after_reload()
    {
        await using TestDatabase db = new();
        FakeHttpMessageHandler handler = new();
        MutableOptionsMonitor<DiscordNotificationConfig> options = new(Config());
        DiscordNotificationClient client = BuildChannel(handler, TestNotifications.UserChannels(db),
            new StubIdentityStore("42"), options);
        await Assert.That(client.CanSendDirectMessages).IsTrue();
        options.Update(new DiscordNotificationConfig { Enabled = false });

        NotificationDeliveryResult direct = await client.SendAsync(Message(), new NotificationRecipient(7));
        NotificationDeliveryResult global = await client.SendAsync(Message(), NotificationRecipient.Global);

        await Assert.That(client.CanSendDirectMessages).IsFalse();
        await Assert.That(direct.Skipped).IsTrue();
        await Assert.That(global.Skipped).IsTrue();
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task Client_applies_transport_exclusions_to_direct_recipients()
    {
        await using TestDatabase db = new();
        FakeHttpMessageHandler handler = new();
        DiscordNotificationConfig config = Config();
        config.Exclude.Add("downloads.#");
        DiscordNotificationClient client = BuildChannel(handler, TestNotifications.UserChannels(db),
            new StubIdentityStore("42"), new MutableOptionsMonitor<DiscordNotificationConfig>(config));

        NotificationDeliveryResult result = await client.SendAsync(Message(), new NotificationRecipient(7));

        await Assert.That(result.Skipped).IsTrue();
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task Channel_delivers_to_an_opted_in_linked_user_and_caches_the_dm()
    {
        await using TestDatabase db = new();
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"id":"dm-1"}""");
        handler.EnqueueJson("""{"id":"message-1"}""");

        UserNotificationChannelService userChannels = TestNotifications.UserChannels(db);
        await userChannels.SetEnabledAsync(7, DiscordNotificationClient.ChannelName, true);
        DiscordNotificationClient channel = BuildChannel(handler, userChannels, new StubIdentityStore("42"));

        NotificationDeliveryResult result = await channel.SendAsync(Message(), new NotificationRecipient(7));

        await Assert.That(result.Success).IsTrue();
        UserNotificationChannel? binding = await userChannels.GetAsync(7, DiscordNotificationClient.ChannelName);
        await Assert.That(binding!.Destination).IsEqualTo("42:dm-1");
        await Assert.That(binding.LastSentAt).IsNotNull();
        await Assert.That(handler.Requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Channel_does_not_reuse_a_dm_from_a_previously_linked_discord_account()
    {
        await using TestDatabase db = new();
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"id":"dm-new"}""");
        handler.EnqueueJson("""{"id":"message-new"}""");

        UserNotificationChannelService userChannels = TestNotifications.UserChannels(db);
        await userChannels.SetEnabledAsync(7, DiscordNotificationClient.ChannelName, true);
        await userChannels.SetDestinationAsync(7, DiscordNotificationClient.ChannelName, "99:dm-old");
        DiscordNotificationClient channel = BuildChannel(handler, userChannels, new StubIdentityStore("42"));

        NotificationDeliveryResult result = await channel.SendAsync(Message(), new NotificationRecipient(7));

        await Assert.That(result.Success).IsTrue();
        await Assert.That(handler.Requests[0].Uri.AbsolutePath).EndsWith("/users/@me/channels");
        await Assert.That(handler.Requests.Any(r => r.Uri.AbsolutePath.Contains("dm-old"))).IsFalse();
    }

    [Test]
    public async Task Channel_delivers_by_default_when_discord_is_linked()
    {
        await using TestDatabase db = new();
        FakeHttpMessageHandler handler = new();
        handler.EnqueueJson("""{"id":"dm-1"}""");
        handler.EnqueueJson("""{"id":"message-1"}""");
        UserNotificationChannelService userChannels = TestNotifications.UserChannels(db);
        DiscordNotificationClient channel = BuildChannel(handler, userChannels, new StubIdentityStore("42"));

        NotificationDeliveryResult result = await channel.SendAsync(Message(), new NotificationRecipient(7));

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Skipped).IsFalse();
        await Assert.That(handler.Requests.Count).IsEqualTo(2);
        UserNotificationChannel? binding = await userChannels.GetAsync(7, DiscordNotificationClient.ChannelName);
        await Assert.That(binding).IsNotNull();
        await Assert.That(binding!.Enabled).IsTrue();
        await Assert.That(binding.Destination).IsEqualTo("42:dm-1");
    }

    [Test]
    public async Task Channel_skips_when_the_user_explicitly_opts_out()
    {
        await using TestDatabase db = new();
        FakeHttpMessageHandler handler = new();
        UserNotificationChannelService userChannels = TestNotifications.UserChannels(db);
        await userChannels.SetEnabledAsync(7, DiscordNotificationClient.ChannelName, false);
        DiscordNotificationClient channel = BuildChannel(handler, userChannels, new StubIdentityStore("42"));

        NotificationDeliveryResult result = await channel.SendAsync(Message(), new NotificationRecipient(7));

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Skipped).IsTrue();
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task Channel_skips_when_discord_is_not_linked()
    {
        await using TestDatabase db = new();
        FakeHttpMessageHandler handler = new();
        UserNotificationChannelService userChannels = TestNotifications.UserChannels(db);
        await userChannels.SetEnabledAsync(7, DiscordNotificationClient.ChannelName, true);
        DiscordNotificationClient channel = BuildChannel(handler, userChannels, new StubIdentityStore(null));

        NotificationDeliveryResult result = await channel.SendAsync(Message(), new NotificationRecipient(7));

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Skipped).IsTrue();
        await Assert.That(handler.Requests).IsEmpty();
    }

    static DiscordNotificationClient BuildChannel(
        FakeHttpMessageHandler handler,
        UserNotificationChannelService userChannels,
        IExternalIdentityStore identities,
        MutableOptionsMonitor<DiscordNotificationConfig>? options = null)
    {
        options ??= new MutableOptionsMonitor<DiscordNotificationConfig>(Config());
        FakeHttpClientFactory factory = new(handler);
        DiscordBotClient bot = new(
            factory,
            options,
            NullLogger<DiscordBotClient>.Instance);
        DiscordWebhookClient webhook = new(factory, NullLogger<DiscordWebhookClient>.Instance);

        ServiceCollection identityServices = new();
        identityServices.AddSingleton(identities);
        ServiceProvider identityProvider = identityServices.BuildServiceProvider();

        return new DiscordNotificationClient(
            webhook,
            bot,
            userChannels,
            TestNotifications.StackInfo(),
            identityProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            TestCatalog.Registry(),
            NullLogger<DiscordNotificationClient>.Instance);
    }

    static NotificationMessage Message() => new()
    {
        Key = DownloadNotificationTopics.DownloadImported,
        Title = "Movie imported",
        Body = "The Matrix is now available",
        Timestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
    };

    sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    sealed class StubIdentityStore(string? discordId) : IExternalIdentityStore
    {
        public Task<IReadOnlyList<UserLoginInfo>> GetExternalLoginsAsync(int userId) =>
            Task.FromResult<IReadOnlyList<UserLoginInfo>>(
                string.IsNullOrWhiteSpace(discordId)
                    ? []
                    : [new UserLoginInfo(ExternalLoginSchemes.Discord, discordId, "Discord")]);

        public Task AddExternalLoginAsync(int userId, string provider, string externalId, string? displayName = null) =>
            Task.CompletedTask;

        public Task<bool> TryAddExternalLoginAsync(
            int userId,
            string provider,
            string externalId,
            string? displayName = null,
            string? avatarUrl = null,
            CancellationToken ct = default) => Task.FromResult(true);

        public Task RemoveExternalLoginAsync(int userId, string provider, string externalId) =>
            Task.CompletedTask;

        public Task<bool> HasPasswordAsync(int userId) => Task.FromResult(true);

        public Task<UserProviderLink?> GetProviderLinkAsync(
            int userId, string provider, CancellationToken ct = default) =>
            Task.FromResult<UserProviderLink?>(null);

        public Task ReconcileLinkedAccountsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
