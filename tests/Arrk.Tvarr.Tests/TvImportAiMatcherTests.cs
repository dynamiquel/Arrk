using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Ai;
using Arrk.Ai.Data;
using Arrk.TestSupport;
using Arrk.Tvarr.Services;

namespace Arrk.Tvarr.Tests;

/// <summary>
/// The local-import AI matcher is additive: it may only choose a title the deterministic matcher
/// already offered, and every disabled/shadow/low-confidence/failed path keeps that deterministic
/// match. These tests pin the same safety rails as the music release ranker.
/// </summary>
public class TvImportAiMatcherTests
{
    static AiConfig Config(string mode = AiRankingModes.Always, double minConfidence = 0.5) => new()
    {
        Enabled = true,
        Provider = AiProviders.Ollama,
        Models = new AiModelsConfig { Medium = "test-model" },
        Features = new AiFeaturesConfig
        {
            ImportMatching = new AiImportMatchingFeature
            {
                Enabled = true,
                Mode = mode,
                MinConfidence = minConfidence,
                MaxCandidates = 5,
                AmbiguityScoreWindow = 8
            }
        }
    };

    static (TvImportAiMatcher Matcher, AiDecisionService Decisions, AiUsageService Usage) Build(
        AiConfig config,
        FakeHttpMessageHandler handler,
        TestDbContextFactory db)
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

        TvImportAiMatcher matcher = new(
            ai,
            decisions,
            monitor,
            NullLogger<TvImportAiMatcher>.Instance);

        return (matcher, decisions, usage);
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

    static List<TvImportCandidate> Tied() =>
    [
        new("movie", "Dune", "", 1984, 1, 0, "", "", 82, [], null, false, [], true),
        new("movie", "Dune", "", 2021, 2, 0, "", "", 80, [], null, false, [], true)
    ];

    static TvParsedFile Parsed => TvImportFilename.Parse("Dune.2021.1080p.mkv");

    [Test]
    public async Task Always_AppliesThePick_AndRecordsTheDecision()
    {
        TestDbContextFactory db = new(TestDb.Options());
        FakeHttpMessageHandler handler = Answer(2, 0.9);
        (TvImportAiMatcher matcher, AiDecisionService decisions, AiUsageService usage) = Build(Config(), handler, db);

        TvImportAiDecision? decision = await matcher.PickAsync(Parsed, Tied());

        await Assert.That(decision).IsNotNull();
        await Assert.That(decision!.Applied).IsTrue();
        await Assert.That(decision.Title).IsEqualTo("Dune");
        await Assert.That(handler.Requests).Count().IsEqualTo(1);
        await Assert.That(handler.Requests[0].Body).Contains("1984");
        await Assert.That(handler.Requests[0].Body).Contains("2021");

        IReadOnlyList<AiDecisionOutcome> log = await decisions.GetRecentAsync();
        await Assert.That(log).Count().IsEqualTo(1);
        await Assert.That(log[0].Applied).IsTrue();
        await Assert.That(log[0].Feature).IsEqualTo(AiFeatureKeys.ImportMatching);

        AiUsageSummary summary = await usage.GetSummaryAsync(DateTime.UtcNow.AddHours(-1));
        await Assert.That(summary.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Shadow_RecordsButDoesNotChangeTheMatch()
    {
        TestDbContextFactory db = new(TestDb.Options());
        FakeHttpMessageHandler handler = Answer(2, 0.95);
        (TvImportAiMatcher matcher, AiDecisionService decisions, _) = Build(
            Config(AiRankingModes.Shadow), handler, db);

        TvImportAiDecision? decision = await matcher.PickAsync(Parsed, Tied());

        await Assert.That(decision).IsNotNull();
        await Assert.That(decision!.Applied).IsFalse();
        await Assert.That(decision.WouldPickDifferent).IsTrue();

        IReadOnlyList<AiDecisionOutcome> log = await decisions.GetRecentAsync();
        await Assert.That(log[0].Applied).IsFalse();
        await Assert.That(log[0].AiChoice).IsEqualTo("Dune");
    }

    [Test]
    public async Task LowConfidence_KeepsTheDeterministicChoice()
    {
        TestDbContextFactory db = new(TestDb.Options());
        FakeHttpMessageHandler handler = Answer(2, 0.2);
        (TvImportAiMatcher matcher, _, _) = Build(Config(minConfidence: 0.8), handler, db);

        TvImportAiDecision? decision = await matcher.PickAsync(Parsed, Tied());

        await Assert.That(decision).IsNotNull();
        await Assert.That(decision!.Applied).IsFalse();
    }

    [Test]
    public async Task Ambiguous_WithAClearWinner_DoesNotCallTheModel()
    {
        TestDbContextFactory db = new(TestDb.Options());
        FakeHttpMessageHandler handler = Answer(2, 0.9);
        (TvImportAiMatcher matcher, _, _) = Build(Config(AiRankingModes.Ambiguous), handler, db);

        List<TvImportCandidate> clear =
        [
            new("movie", "Dune", "", 2021, 2, 0, "", "", 96, [], null, false, [], true),
            new("movie", "Dune", "", 1984, 1, 0, "", "", 40, [], null, false, [], true)
        ];

        TvImportAiDecision? decision = await matcher.PickAsync(Parsed, clear);

        await Assert.That(decision).IsNull();
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task Ambiguous_WithATie_CallsTheModel()
    {
        TestDbContextFactory db = new(TestDb.Options());
        FakeHttpMessageHandler handler = Answer(2, 0.9);
        (TvImportAiMatcher matcher, _, _) = Build(Config(AiRankingModes.Ambiguous), handler, db);

        TvImportAiDecision? decision = await matcher.PickAsync(Parsed, Tied());

        await Assert.That(decision).IsNotNull();
        await Assert.That(decision!.Applied).IsTrue();
        await Assert.That(handler.Requests).Count().IsEqualTo(1);
    }

    [Test]
    public async Task InvalidPick_IsIgnored()
    {
        TestDbContextFactory db = new(TestDb.Options());
        FakeHttpMessageHandler handler = Answer(99, 0.9);
        (TvImportAiMatcher matcher, _, _) = Build(Config(), handler, db);

        await Assert.That(await matcher.PickAsync(Parsed, Tied())).IsNull();
    }

    [Test]
    public async Task UnparseableAnswer_KeepsTheDeterministicChoice()
    {
        TestDbContextFactory db = new(TestDb.Options());
        (TvImportAiMatcher matcher, _, _) = Build(Config(), Broken(), db);

        await Assert.That(await matcher.PickAsync(Parsed, Tied())).IsNull();
    }

    [Test]
    public async Task DisabledFeature_NeverCallsTheModel()
    {
        TestDbContextFactory db = new(TestDb.Options());
        FakeHttpMessageHandler handler = Answer(2, 0.9);
        AiConfig config = Config();
        config.Features.ImportMatching.Enabled = false;
        (TvImportAiMatcher matcher, _, _) = Build(config, handler, db);

        await Assert.That(await matcher.PickAsync(Parsed, Tied())).IsNull();
        await Assert.That(handler.Requests).IsEmpty();
    }
}
