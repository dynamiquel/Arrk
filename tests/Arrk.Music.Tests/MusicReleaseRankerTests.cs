using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Ai;
using Arrk.Ai.Data;
using Arrk.Music.Services;
using Arrk.TestSupport;

namespace Arrk.Music.Tests;

/// <summary>
/// The music ranker is an additive tie-breaker: it only ever chooses between candidates that already
/// passed the deterministic matcher, and every failure/abstention/disabled path leaves the current
/// choice untouched. These tests pin the model contract and the shadow/ambiguous safety rails.
/// </summary>
public class MusicReleaseRankerTests
{
    static AiConfig Config(string mode = AiRankingModes.Always, double minConfidence = 0.5) => new()
    {
        Enabled = true,
        Provider = AiProviders.Ollama,
        Models = new AiModelsConfig { Medium = "test-model" },
        Features = new AiFeaturesConfig
        {
            ReleaseRanking = new AiReleaseRankingFeature
            {
                Enabled = true,
                Mode = mode,
                MinConfidence = minConfidence,
                MaxCandidates = 5,
                AmbiguityScoreWindow = 5
            }
        }
    };

    static (MusicReleaseRanker Ranker, AiDecisionService Decisions, AiUsageService Usage, MutableOptionsMonitor<AiConfig> Monitor)
        Build(AiConfig config, FakeHttpMessageHandler handler, TestMusicDb db)
    {
        MutableOptionsMonitor<AiConfig> monitor = new(config);
        AiUsageService usage = new(db, TimeProvider.System);
        AiBudget budget = new(usage, TimeProvider.System);
        AiDecisionService decisions = new(db, TimeProvider.System);
        AiService ai = new(
            new AiClientFactory(NullLogger<AiClientFactory>.Instance, handler),
            monitor,
            NullLogger<AiService>.Instance,
            usage,
            budget);

        MusicLibrary library = MusicTestHelpers.Library(db);
        MusicReleaseRanker ranker = new(
            ai,
            decisions,
            library,
            monitor,
            NullLogger<MusicReleaseRanker>.Instance);

        return (ranker, decisions, usage, monitor);
    }

    static FakeHttpMessageHandler Answer(int pick, double confidence, string reason = "matches the target")
    {
        string content = JsonSerializer.Serialize(new { pick, confidence, reason });
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(ChatCompletion(content)));
        return handler;
    }

    static FakeHttpMessageHandler Broken()
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse(ChatCompletion("not json")));
        return handler;
    }

    static string ChatCompletion(string content) => $$"""
        {
          "id":"x","object":"chat.completion","created":1,"model":"m",
          "choices":[{"index":0,"message":{"role":"assistant","content":{{JsonSerializer.Serialize(content)}}},"finish_reason":"stop"}],
          "usage":{"prompt_tokens":120,"completion_tokens":18,"total_tokens":138}
        }
        """;

    static List<MusicRelease> Tied() =>
    [
        new() { Title = "Album (2011 Remaster)", Indexer = "A", Score = 100, Seeders = 5, IsLossless = true, Coverage = 1.0 },
        new() { Title = "Album", Indexer = "B", Score = 100, Seeders = 40, IsLossless = true, Coverage = 1.0 }
    ];

    [Test]
    public async Task Always_AppliesThePick_AndRecordsTheDecisionAndUsage()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Answer(2, 0.9);
        (MusicReleaseRanker ranker, AiDecisionService decisions, AiUsageService usage, _) = Build(Config(), handler, db);

        MusicRelease? picked = await ranker.PickAsync(0, "Artist", "Album", isTrack: false, Tied());

        await Assert.That(picked).IsNotNull();
        await Assert.That(picked!.Title).IsEqualTo("Album");
        await Assert.That(handler.Requests).Count().IsEqualTo(1);
        await Assert.That(handler.Requests[0].Body).Contains("Candidates");
        await Assert.That(handler.Requests[0].Body).Contains("2011 Remaster");

        IReadOnlyList<AiDecisionOutcome> log = await decisions.GetRecentAsync();
        await Assert.That(log).Count().IsEqualTo(1);
        await Assert.That(log[0].Applied).IsTrue();
        await Assert.That(log[0].AiChoice).IsEqualTo("Album");
        await Assert.That(log[0].DeterministicChoice).IsEqualTo("Album (2011 Remaster)");

        AiUsageSummary summary = await usage.GetSummaryAsync(DateTime.UtcNow.AddHours(-1));
        await Assert.That(summary.Calls).IsEqualTo(1);
        await Assert.That(summary.TotalTokens).IsGreaterThan(0);
    }

    [Test]
    public async Task Shadow_RecordsThePick_ButNeverChangesIt()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Answer(2, 0.95);
        (MusicReleaseRanker ranker, AiDecisionService decisions, _, _) = Build(Config(AiRankingModes.Shadow), handler, db);

        MusicRelease? picked = await ranker.PickAsync(0, "Artist", "Album", isTrack: false, Tied());

        await Assert.That(picked).IsNull();
        IReadOnlyList<AiDecisionOutcome> log = await decisions.GetRecentAsync();
        await Assert.That(log).Count().IsEqualTo(1);
        await Assert.That(log[0].Applied).IsFalse();
        await Assert.That(log[0].AiChoice).IsEqualTo("Album");
    }

    [Test]
    public async Task LowConfidence_KeepsTheDeterministicChoice()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Answer(2, 0.2);
        (MusicReleaseRanker ranker, AiDecisionService decisions, _, _) = Build(Config(minConfidence: 0.8), handler, db);

        MusicRelease? picked = await ranker.PickAsync(0, "Artist", "Album", isTrack: false, Tied());

        await Assert.That(picked).IsNull();
        IReadOnlyList<AiDecisionOutcome> log = await decisions.GetRecentAsync();
        await Assert.That(log[0].Applied).IsFalse();
    }

    [Test]
    public async Task Ambiguous_WithAClearWinner_DoesNotCallTheModel()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Answer(2, 0.9);
        (MusicReleaseRanker ranker, _, _, _) = Build(Config(AiRankingModes.Ambiguous), handler, db);

        MusicRelease clearWinner = new() { Title = "Album", Indexer = "A", Score = 100, Coverage = 1.0 };
        MusicRelease runnerUp = new() { Title = "Album Disc 2", Indexer = "B", Score = 60, Coverage = 1.0 };

        MusicRelease? picked = await ranker.PickAsync(0, "Artist", "Album", isTrack: false, [clearWinner, runnerUp]);

        await Assert.That(picked).IsNull();
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task Ambiguous_WithATie_CallsTheModel()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Answer(2, 0.9);
        (MusicReleaseRanker ranker, _, _, _) = Build(Config(AiRankingModes.Ambiguous), handler, db);

        MusicRelease? picked = await ranker.PickAsync(0, "Artist", "Album", isTrack: false, Tied());

        await Assert.That(picked).IsNotNull();
        await Assert.That(picked!.Title).IsEqualTo("Album");
    }

    [Test]
    public async Task InvalidPick_IsIgnored()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Answer(99, 0.9);
        (MusicReleaseRanker ranker, _, _, _) = Build(Config(), handler, db);

        MusicRelease? picked = await ranker.PickAsync(0, "Artist", "Album", isTrack: false, Tied());

        await Assert.That(picked).IsNull();
    }

    [Test]
    public async Task UnparseableAnswer_KeepsTheDeterministicChoice()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Broken();
        (MusicReleaseRanker ranker, _, _, _) = Build(Config(), handler, db);

        MusicRelease? picked = await ranker.PickAsync(0, "Artist", "Album", isTrack: false, Tied());

        await Assert.That(picked).IsNull();
    }

    [Test]
    public async Task DisabledFeature_NeverCallsTheModel()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Answer(2, 0.9);
        AiConfig config = Config();
        config.Features.ReleaseRanking.Enabled = false;
        (MusicReleaseRanker ranker, _, _, _) = Build(config, handler, db);

        MusicRelease? picked = await ranker.PickAsync(0, "Artist", "Album", isTrack: false, Tied());

        await Assert.That(picked).IsNull();
        await Assert.That(handler.Requests).IsEmpty();
    }
}
