using Stackarr.Ai.Data;

namespace Stackarr.Ai.Tests;

/// <summary>
/// Per-user assistant limits. They are advisory guardrails, so the important properties are: zero
/// means unlimited, a limit stops the turn before the model is called, and the windows actually roll
/// over. One turn (with any tool iterations) is one usage row, which is why these count rows.
/// </summary>
public class AssistantRateLimiterTests
{
    static AiConfig Config(int messagesPerHour = 0, long tokensPerDay = 0) => new()
    {
        Enabled = true,
        Provider = AiProviders.Ollama,
        Models = new AiModelsConfig { Chat = "m" },
        Features = new AiFeaturesConfig
        {
            Assistant = new AiAssistantFeature
            {
                MaxMessagesPerHour = messagesPerHour,
                MaxTokensPerUserPerDay = tokensPerDay
            }
        }
    };

    [Test]
    public async Task Unlimited_WhenBothCapsAreZero()
    {
        using AssistantTestHost host = new(Config());

        AssistantLimitStatus status = await host.Limiter.CheckAsync(host.Config, userId: 1);

        await Assert.That(status.Allowed).IsTrue();
        await Assert.That(status.MessagesPerHourLimit).IsNull();
        await Assert.That(status.TokensPerDayLimit).IsNull();
    }

    [Test]
    public async Task HourlyMessageCap_BlocksTheNextMessage()
    {
        AiConfig config = Config(messagesPerHour: 2);
        using AssistantTestHost host = new(config);
        for (int i = 0; i < 2; i++)
            await host.Usage.RecordAsync(Row(host, config, userId: 1, tokens: 1));

        AssistantLimitStatus status = await host.Limiter.CheckAsync(config, userId: 1);

        await Assert.That(status.Allowed).IsFalse();
        await Assert.That(status.Message).Contains("messages per hour");
        await Assert.That(status.MessagesThisHour).IsEqualTo(2);
    }

    [Test]
    public async Task HourlyCap_RollsOver_ButTheDailyTokenCapDoesNot()
    {
        AiConfig config = Config(messagesPerHour: 1, tokensPerDay: 100);
        using AssistantTestHost host = new(config);
        await host.Usage.RecordAsync(Row(host, config, userId: 1, tokens: 60));

        await Assert.That((await host.Limiter.CheckAsync(config, 1)).Allowed).IsFalse();

        host.Time.Advance(TimeSpan.FromHours(2));

        AssistantLimitStatus after = await host.Limiter.CheckAsync(config, 1);
        await Assert.That(after.Allowed).IsTrue();
        await Assert.That(after.MessagesThisHour).IsEqualTo(0);
        await Assert.That(after.TokensToday).IsEqualTo(60);
    }

    [Test]
    public async Task DailyTokenCap_BlocksWhenReached()
    {
        AiConfig config = Config(tokensPerDay: 100);
        using AssistantTestHost host = new(config);
        await host.Usage.RecordAsync(Row(host, config, userId: 1, tokens: 60));
        await host.Usage.RecordAsync(Row(host, config, userId: 1, tokens: 50));

        AssistantLimitStatus status = await host.Limiter.CheckAsync(config, 1);

        await Assert.That(status.Allowed).IsFalse();
        await Assert.That(status.Message).Contains("token limit");
        await Assert.That(status.TokensToday).IsEqualTo(110);
    }

    [Test]
    public async Task AnotherUsersUsage_DoesNotCount()
    {
        AiConfig config = Config(messagesPerHour: 1);
        using AssistantTestHost host = new(config);
        await host.Usage.RecordAsync(Row(host, config, userId: 2, tokens: 10));

        AssistantLimitStatus status = await host.Limiter.CheckAsync(config, userId: 1);

        await Assert.That(status.Allowed).IsTrue();
    }

    static AiUsageRecord Row(AssistantTestHost host, AiConfig config, int userId, long tokens) =>
        host.Usage.CreateRecord(config, AiFeatureKeys.Assistant, "chat", AiModelRole.Chat,
            tokens, 0, 10, success: true, cacheHit: false, userId: userId,
            error: null, promptText: null, responseText: null);
}
