using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Ai;
using Arrk.Music.Tools;

namespace Arrk.Music.Tests;

/// <summary>
/// The music module's assistant packs. They expose only <c>music_…</c> tools and a runner scoped to
/// the music action source, so they cannot touch a TV or plugin id.
/// </summary>
public class MusicAssistantToolTests
{
    static AssistantToolContext Context() => new(
        1, "alice", false, new ServiceCollection().BuildServiceProvider(), CancellationToken.None);

    static AIFunction Tool(IAssistantTool pack, string name) =>
        (AIFunction)pack.CreateTools(Context()).Single(t => t.Name == name);

    [Test]
    public async Task MusicActionPack_PrefixesItsToolsAndRoutesToTheMusicSource()
    {
        FakeActionSource source = new("music");
        MusicAssistantActionsTool pack = new(new AssistantActionRunner([source]));

        await Assert.That(pack.CreateTools(Context()).All(t => t.Name.StartsWith("music_", StringComparison.Ordinal))).IsTrue();

        JsonElement json = (JsonElement)(await Tool(pack, "music_request_media").InvokeAsync(
            new AIFunctionArguments { ["id"] = "music:album:dz:42" }))!;

        await Assert.That(json.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(source.Calls.Single().Id).IsEqualTo("music:album:dz:42");
    }

    [Test]
    public async Task MusicActionPack_RejectsTvIds()
    {
        FakeActionSource source = new("music");
        MusicAssistantActionsTool pack = new(new AssistantActionRunner([source]));

        JsonElement json = (JsonElement)(await Tool(pack, "music_play_media").InvokeAsync(
            new AIFunctionArguments { ["id"] = "tv:movie:1" }))!;

        await Assert.That(json.GetProperty("ok").GetBoolean()).IsFalse();
        await Assert.That(source.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MusicActionPack_DoesNotExposeAQualityUpgrade()
    {
        MusicAssistantActionsTool pack = new(new AssistantActionRunner([new FakeActionSource("music")]));

        await Assert.That(pack.CreateTools(Context()).Any(t => t.Name.Contains("upgrade", StringComparison.Ordinal)))
            .IsFalse();
    }

    [Test]
    public async Task MusicRecommendationPack_AsksForMusicAndReturnsReasons()
    {
        FakeRecommendationSource source = new();
        MusicAssistantRecommendationsTool pack = new(source);

        JsonElement json = (JsonElement)(await Tool(pack, "music_recommend_media").InvokeAsync(
            new AIFunctionArguments { ["genre"] = "Jazz", ["limit"] = 2 }))!;

        await Assert.That(source.LastQuery!.Domain).IsEqualTo("music");
        await Assert.That(source.LastQuery.Genre).IsEqualTo("Jazz");
        await Assert.That(json.GetProperty("items")[0].GetProperty("reason").GetString())
            .IsEqualTo("Charting now");
    }

    sealed class FakeActionSource(string key) : IAssistantMediaActionSource
    {
        public string Key { get; } = key;
        public bool IsAvailable => true;
        public List<(AssistantActionKind Action, string Id)> Calls { get; } = [];

        public IReadOnlyList<AssistantActionKind> SupportedActions =>
        [
            AssistantActionKind.Request,
            AssistantActionKind.AddToWatchlist,
            AssistantActionKind.RemoveFromWatchlist,
            AssistantActionKind.Delete,
            AssistantActionKind.Play
        ];

        public Task<AssistantActionOutcome> ExecuteAsync(
            AssistantActionKind action, string id, string? option, bool confirmed,
            AssistantToolContext context, CancellationToken ct)
        {
            Calls.Add((action, id));
            return Task.FromResult(AssistantActionOutcome.Ok("done"));
        }
    }

    sealed class FakeRecommendationSource : IAssistantRecommendationSource
    {
        public string Key => "music";
        public bool IsAvailable => true;
        public AssistantRecommendationQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<AssistantRecommendation>> RecommendAsync(
            AssistantRecommendationQuery query, CancellationToken ct)
        {
            LastQuery = query;
            AssistantMediaSummary item = new(
                "music", "music:album:dz:1", AssistantMediaKind.Album, "Blue Lines", "Massive Attack",
                1991, "Not in library");
            return Task.FromResult<IReadOnlyList<AssistantRecommendation>>(
                [new AssistantRecommendation(item, "Charting now", 90)]);
        }
    }
}
