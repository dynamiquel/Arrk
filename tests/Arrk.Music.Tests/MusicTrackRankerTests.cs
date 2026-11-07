using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Ai;
using Arrk.Ai.Data;
using Arrk.Music.Services;
using Arrk.TestSupport;

namespace Arrk.Music.Tests;

/// <summary>
/// The single-track ranker is the Soulseek tie-breaker: it only chooses between files that already
/// passed the filename/artist/version filters, and every disabled/shadow/failed/low-confidence path
/// keeps the deterministic best file.
/// </summary>
public class MusicTrackRankerTests
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

    static (MusicReleaseRanker Ranker, AiDecisionService Decisions, AiUsageService Usage)
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

        MusicReleaseRanker ranker = new(
            ai,
            decisions,
            MusicTestHelpers.Library(db),
            monitor,
            NullLogger<MusicReleaseRanker>.Instance);

        return (ranker, decisions, usage);
    }

    static FakeHttpMessageHandler Answer(int pick, double confidence) =>
        Handler(JsonSerializer.Serialize(new { pick, confidence, reason = "best match" }));

    static FakeHttpMessageHandler Handler(string content)
    {
        FakeHttpMessageHandler handler = new();
        handler.Fallback = (_, _) => Task.FromResult(FakeHttpMessageHandler.JsonResponse($$"""
            {
              "id":"x","object":"chat.completion","created":1,"model":"m",
              "choices":[{"index":0,"message":{"role":"assistant","content":{{JsonSerializer.Serialize(content)}}},"finish_reason":"stop"}],
              "usage":{"prompt_tokens":90,"completion_tokens":12,"total_tokens":102}
            }
            """));
        return handler;
    }

    static List<MusicTrackCandidate> Tied() =>
    [
        new("peer-a", "Music\\Artist\\Album\\01 - Song.flac", 30_000_000, 0, 44100, 16, 240, true, 0, 1000, true),
        new("peer-b", "Music\\Artist\\Album\\01 - Song (Remastered).flac", 40_000_000, 0, 96000, 24, 241, true, 0, 1000, true)
    ];

    [Test]
    public async Task TrackAlways_AppliesThePick_AndRecordsTheDecision()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Answer(2, 0.92);
        (MusicReleaseRanker ranker, AiDecisionService decisions, AiUsageService usage) = Build(Config(), handler, db);

        MusicTrackCandidate? picked = await ranker.PickTrackAsync(0, "Artist", "Song", 123, Tied());

        await Assert.That(picked).IsNotNull();
        await Assert.That(picked!.Peer).IsEqualTo("peer-b");
        await Assert.That(handler.Requests[0].Body).Contains("Soulseek");
        await Assert.That(handler.Requests[0].Body).Contains("Remastered");

        IReadOnlyList<AiDecisionOutcome> log = await decisions.GetRecentAsync();
        await Assert.That(log).Count().IsEqualTo(1);
        await Assert.That(log[0].Operation).IsEqualTo("music.trackRank");
        await Assert.That(log[0].Subject).IsEqualTo("track:123");
        await Assert.That(log[0].Applied).IsTrue();
        await Assert.That(log[0].AiChoice).IsEqualTo("01 - Song (Remastered).flac");

        AiUsageSummary summary = await usage.GetSummaryAsync(DateTime.UtcNow.AddHours(-1));
        await Assert.That(summary.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task TrackShadow_RecordsButNeverChangesTheChoice()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Answer(2, 0.95);
        (MusicReleaseRanker ranker, AiDecisionService decisions, _) = Build(Config(AiRankingModes.Shadow), handler, db);

        MusicTrackCandidate? picked = await ranker.PickTrackAsync(0, "Artist", "Song", 123, Tied());

        await Assert.That(picked).IsNull();
        IReadOnlyList<AiDecisionOutcome> log = await decisions.GetRecentAsync();
        await Assert.That(log[0].Applied).IsFalse();
        await Assert.That(log[0].AiChoice).IsEqualTo("01 - Song (Remastered).flac");
    }

    [Test]
    public async Task TrackLowConfidence_KeepsTheDeterministicFile()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Answer(2, 0.1);
        (MusicReleaseRanker ranker, _, _) = Build(Config(minConfidence: 0.8), handler, db);

        MusicTrackCandidate? picked = await ranker.PickTrackAsync(0, "Artist", "Song", 123, Tied());

        await Assert.That(picked).IsNull();
    }

    [Test]
    public async Task TrackAmbiguous_WithAClearWinner_DoesNotCallTheModel()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Answer(2, 0.9);
        (MusicReleaseRanker ranker, _, _) = Build(Config(AiRankingModes.Ambiguous), handler, db);

        List<MusicTrackCandidate> candidates =
        [
            new("peer-a", "a\\song.flac", 30_000_000, 0, 44100, 16, 240, true, 0, 1000, true),
            new("peer-b", "b\\song.mp3", 4_000_000, 192, 44100, 16, 240, true, 0, 192, false)
        ];

        MusicTrackCandidate? picked = await ranker.PickTrackAsync(0, "Artist", "Song", 123, candidates);

        await Assert.That(picked).IsNull();
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task TrackAmbiguous_TwoLosslessFiles_CallsTheModel()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Answer(2, 0.9);
        (MusicReleaseRanker ranker, _, _) = Build(Config(AiRankingModes.Ambiguous), handler, db);

        MusicTrackCandidate? picked = await ranker.PickTrackAsync(0, "Artist", "Song", 123, Tied());

        await Assert.That(picked).IsNotNull();
        await Assert.That(picked!.Peer).IsEqualTo("peer-b");
    }

    [Test]
    public async Task TrackInvalidPick_IsIgnored()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Answer(99, 0.9);
        (MusicReleaseRanker ranker, _, _) = Build(Config(), handler, db);

        MusicTrackCandidate? picked = await ranker.PickTrackAsync(0, "Artist", "Song", 123, Tied());

        await Assert.That(picked).IsNull();
    }

    [Test]
    public async Task TrackUnparseableAnswer_KeepsTheDeterministicFile()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Handler("not json");
        (MusicReleaseRanker ranker, _, _) = Build(Config(), handler, db);

        MusicTrackCandidate? picked = await ranker.PickTrackAsync(0, "Artist", "Song", 123, Tied());

        await Assert.That(picked).IsNull();
    }

    [Test]
    public async Task TrackDisabled_NeverCallsTheModel()
    {
        using TestMusicDb db = new();
        FakeHttpMessageHandler handler = Answer(2, 0.9);
        AiConfig config = Config();
        config.Features.ReleaseRanking.Enabled = false;
        (MusicReleaseRanker ranker, _, _) = Build(config, handler, db);

        MusicTrackCandidate? picked = await ranker.PickTrackAsync(0, "Artist", "Song", 123, Tied());

        await Assert.That(picked).IsNull();
        await Assert.That(handler.Requests).IsEmpty();
    }
}
