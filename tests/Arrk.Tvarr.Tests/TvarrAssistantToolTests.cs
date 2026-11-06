using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Ai;
using Arrk.Tvarr.Tools;

namespace Arrk.Tvarr.Tests;

/// <summary>
/// The TV module's assistant packs. They only expose <c>tv_…</c> tool names and a runner scoped to
/// the TV action source, so a TV tool can never act on a music or plugin id.
/// </summary>
public class TvarrAssistantToolTests
{
    static AssistantToolContext Context(bool admin = false, IList<AssistantActionLink>? links = null) => new(
        1, "alice", admin, new ServiceCollection().BuildServiceProvider(), CancellationToken.None, links);

    static AIFunction Tool(IAssistantTool pack, string name, AssistantToolContext? context = null) =>
        (AIFunction)pack.CreateTools(context ?? Context()).Single(t => t.Name == name);

    [Test]
    public async Task TvActionPack_PrefixesItsToolsAndRoutesToTheTvSource()
    {
        FakeActionSource source = new("tv");
        TvarrAssistantActionsTool pack = new(new AssistantActionRunner([source]));

        await Assert.That(pack.CreateTools(Context()).All(t => t.Name.StartsWith("tv_", StringComparison.Ordinal))).IsTrue();

        JsonElement json = (JsonElement)(await Tool(pack, "tv_request_media").InvokeAsync(
            new AIFunctionArguments { ["id"] = "tv:movie:603" }))!;

        await Assert.That(json.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(source.Calls.Single().Id).IsEqualTo("tv:movie:603");
    }

    [Test]
    public async Task TvActionPack_RejectsMusicIds()
    {
        FakeActionSource source = new("tv");
        TvarrAssistantActionsTool pack = new(new AssistantActionRunner([source]));

        JsonElement json = (JsonElement)(await Tool(pack, "tv_request_media").InvokeAsync(
            new AIFunctionArguments { ["id"] = "music:album:dz:1" }))!;

        await Assert.That(json.GetProperty("ok").GetBoolean()).IsFalse();
        await Assert.That(source.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TvActionPack_DeleteIsAdminOnly()
    {
        FakeActionSource source = new("tv");
        TvarrAssistantActionsTool pack = new(new AssistantActionRunner([source]));

        JsonElement json = (JsonElement)(await Tool(pack, "tv_delete_media").InvokeAsync(
            new AIFunctionArguments { ["id"] = "tv:movie:603" }))!;

        await Assert.That(json.GetProperty("ok").GetBoolean()).IsFalse();
        await Assert.That(json.GetProperty("message").GetString()).Contains("administrator");
        await Assert.That(source.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TvRecommendationPack_ForwardsAnimeAndReturnsReasons()
    {
        FakeRecommendationSource source = new();
        TvarrAssistantRecommendationsTool pack = new(source);

        JsonElement json = (JsonElement)(await Tool(pack, "tv_recommend_media").InvokeAsync(
            new AIFunctionArguments { ["domain"] = "anime", ["limit"] = 3 }))!;

        await Assert.That(source.LastQuery!.Anime).IsTrue();
        await Assert.That(json.GetProperty("items")[0].GetProperty("reason").GetString())
            .IsEqualTo("Airing this season");
    }

    [Test]
    public async Task TvRecommendationPack_RejectsMusicDomain()
    {
        FakeRecommendationSource source = new();
        TvarrAssistantRecommendationsTool pack = new(source);

        JsonElement json = (JsonElement)(await Tool(pack, "tv_recommend_media").InvokeAsync(
            new AIFunctionArguments { ["domain"] = "music" }))!;

        await Assert.That(source.Calls).IsEqualTo(0);
        await Assert.That(json.GetProperty("items").GetArrayLength()).IsEqualTo(0);
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
            AssistantActionKind.Upgrade,
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
        public string Key => "tv";
        public bool IsAvailable => true;
        public int Calls { get; private set; }
        public AssistantRecommendationQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<AssistantRecommendation>> RecommendAsync(
            AssistantRecommendationQuery query, CancellationToken ct)
        {
            Calls++;
            LastQuery = query;
            AssistantMediaSummary item = new(
                "tv", "tv:series:1", AssistantMediaKind.Series, "Frieren", "Series", 2023, "Not in library");
            return Task.FromResult<IReadOnlyList<AssistantRecommendation>>(
                [new AssistantRecommendation(item, "Airing this season", 50)]);
        }
    }
}
