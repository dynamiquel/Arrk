using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Core.Configuration;
using Arrk.Notifications.Discord;
using Arrk.TestSupport;
using NotificationDiscordConfig = Arrk.Core.Configuration.DiscordConfig;

namespace Arrk.Notifications.Tests;

/// <summary>
/// Env-gated live checks for the Discord bot. Set <c>ARRK_DISCORD_BOT_TOKEN</c> to validate
/// the token; set <c>ARRK_DISCORD_TEST_USER_ID</c> as well to send a real DM. Unset variables
/// or <c>ARRK_SKIP_LIVE_TESTS=1</c> skip the test.
/// </summary>
public class LiveDiscordDirectMessageSmokeTests
{
    static string? Token() => Environment.GetEnvironmentVariable("ARRK_DISCORD_BOT_TOKEN");

    static string? TestUserId() => Environment.GetEnvironmentVariable("ARRK_DISCORD_TEST_USER_ID");

    static bool Skip() =>
        Environment.GetEnvironmentVariable("ARRK_SKIP_LIVE_TESTS") == "1"
        || string.IsNullOrWhiteSpace(Token());

    static DiscordBotClient CreateClient()
    {
        ServiceCollection services = new();
        services.AddHttpClient(DiscordBotClient.ClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(15));
        ServiceProvider provider = services.BuildServiceProvider();

        NotificationConfig config = new()
        {
            Discord = new NotificationDiscordConfig
            {
                Enabled = true,
                DirectMessages = new DiscordDirectMessageConfig
                {
                    Enabled = true,
                    BotToken = Token()!,
                    ApiBaseUrl = "https://discord.com/api/v10"
                }
            }
        };

        return new DiscordBotClient(
            provider.GetRequiredService<IHttpClientFactory>(),
            new MutableOptionsMonitor<NotificationConfig>(config),
            NullLogger<DiscordBotClient>.Instance);
    }

    [Test]
    public async Task Discord_BotToken_ResolvesBotUser()
    {
        if (Skip())
            return;

        DiscordBotInfo? bot = await CreateClient().GetBotAsync();

        await Assert.That(bot).IsNotNull();
        await Assert.That(bot!.Id).IsNotEmpty();
    }

    [Test]
    public async Task Discord_RealDirectMessage_AcceptsEmbed()
    {
        if (Skip() || string.IsNullOrWhiteSpace(TestUserId()))
            return;

        DiscordDirectMessageResult result = await CreateClient().SendDirectMessageAsync(
            TestUserId()!,
            new DiscordMessagePayload
            {
                Embeds =
                [
                    new DiscordEmbed
                    {
                        Title = "Arrk direct message test",
                        Description = "If you can read this, Discord DMs are wired up.",
                        Color = 0x2ECC71
                    }
                ]
            },
            existingChannelId: null);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Error).IsNull();
    }
}
