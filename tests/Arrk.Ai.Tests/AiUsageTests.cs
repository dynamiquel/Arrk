using Arrk.Ai.Data;

namespace Arrk.Ai.Tests;

/// <summary>
/// Usage accounting is what makes AI spend visible. These pin the reporter's contract: every call
/// is attributed to a feature, failures are distinguishable from successes, and prompt text is only
/// stored when privacy.storePromptText is on.
/// </summary>
public class AiUsageTests
{
    static AiConfig Config(bool storePrompt = false) => new()
    {
        Enabled = true,
        Provider = AiProviders.Ollama,
        Models = new AiModelsConfig { Medium = "m" },
        Privacy = new AiPrivacyConfig { StorePromptText = storePrompt }
    };

    [Test]
    public async Task Summary_GroupsByFeature_AndCountsFailuresAndTokens()
    {
        using TestAiDb db = new();
        AiUsageService usage = new(db, TimeProvider.System);
        AiConfig config = Config();

        await usage.RecordAsync(usage.CreateRecord(config, "releaseRanking", "music.releaseRank", AiModelRole.Low,
            100, 20, 300, success: true, cacheHit: false, userId: null, error: null, "p", "r"));
        await usage.RecordAsync(usage.CreateRecord(config, "releaseRanking", "music.releaseRank", AiModelRole.Low,
            80, 10, 200, success: false, cacheHit: false, userId: null, error: "boom", "p", null));
        await usage.RecordAsync(usage.CreateRecord(config, AiFeatureKeys.Assistant, "chat", AiModelRole.Medium,
            300, 60, 900, success: true, cacheHit: false, userId: 7, error: null, "p", "r"));

        AiUsageSummary summary = await usage.GetSummaryAsync(DateTime.UtcNow.AddHours(-1));

        await Assert.That(summary.Calls).IsEqualTo(3);
        await Assert.That(summary.Failures).IsEqualTo(1);
        await Assert.That(summary.TotalTokens).IsEqualTo(570);
        await Assert.That(summary.Features).Count().IsEqualTo(2);

        AiUsageFeatureSummary ranker = summary.Features.Single(x => x.Feature == "releaseRanking");
        await Assert.That(ranker.Calls).IsEqualTo(2);
        await Assert.That(ranker.Failures).IsEqualTo(1);
    }

    [Test]
    public async Task PromptText_IsOnlyStoredWhenEnabled()
    {
        using TestAiDb db = new();
        AiUsageService usage = new(db, TimeProvider.System);

        await usage.RecordAsync(usage.CreateRecord(Config(storePrompt: false), "assistant", "chat",
            AiModelRole.Medium, 1, 1, 1, true, false, 1, null, "SECRET-PROMPT", "ANSWER"));

        IReadOnlyList<AiUsageRecord> recent = await usage.GetRecentAsync();
        await Assert.That(recent[0].PromptText).IsNull();
        await Assert.That(recent[0].ResponseText).IsNull();

        await usage.RecordAsync(usage.CreateRecord(Config(storePrompt: true), "assistant", "chat",
            AiModelRole.Medium, 1, 1, 1, true, false, 1, null, "SECRET-PROMPT", "ANSWER"));

        recent = await usage.GetRecentAsync();
        await Assert.That(recent[0].PromptText).IsEqualTo("SECRET-PROMPT");
    }

    [Test]
    public async Task Budget_BlocksOnceTheDailyRequestCapIsReached()
    {
        using TestAiDb db = new();
        AiUsageService usage = new(db, TimeProvider.System);
        AiConfig config = Config();
        config.Limits.MaxRequestsPerDay = 2;
        config.Limits.MaxTokensPerMonth = 0;
        AiBudget budget = new(usage, TimeProvider.System);

        await usage.RecordAsync(usage.CreateRecord(config, "assistant", "chat", AiModelRole.Medium,
            1, 1, 1, true, false, 1, null, null, null));
        await usage.RecordAsync(usage.CreateRecord(config, "assistant", "chat", AiModelRole.Medium,
            1, 1, 1, true, false, 1, null, null, null));

        await Assert.That(await budget.CanCallAsync(config)).IsFalse();
        await Assert.That(await budget.RemainingRequestsTodayAsync(config)).IsEqualTo(0);
    }

    [Test]
    public async Task Budget_UnlimitedWhenTheCapIsZero()
    {
        using TestAiDb db = new();
        AiUsageService usage = new(db, TimeProvider.System);
        AiConfig config = Config();
        config.Limits.MaxRequestsPerDay = 0;
        AiBudget budget = new(usage, TimeProvider.System);

        await Assert.That(await budget.CanCallAsync(config)).IsTrue();
        await Assert.That(await budget.RemainingRequestsTodayAsync(config)).IsNull();
    }

    [Test]
    public async Task Cache_ExpiresEntries()
    {
        using TestAiDb db = new();
        TestTimeProvider clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        AiCache cache = new(db, clock);
        string key = AiCache.Key("releaseRanking", "m", "prompt");

        await cache.SetAsync(key, "releaseRanking", "answer", TimeSpan.FromMinutes(10));
        await Assert.That(await cache.TryGetAsync(key)).IsEqualTo("answer");

        clock.Advance(TimeSpan.FromMinutes(11));
        await Assert.That(await cache.TryGetAsync(key)).IsNull();
    }
}
