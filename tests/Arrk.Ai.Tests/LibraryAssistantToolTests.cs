using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Ai.Tools;
using Arrk.Core.Enums;
using Arrk.Core.Playback;

namespace Arrk.Ai.Tests;

/// <summary>
/// The cross-domain library pack. These pin the uniform id scheme, the domain filter and the
/// watch-progress fallback, all without a model: the tools are plain functions over the sources.
/// </summary>
public class LibraryAssistantToolTests
{
    static AssistantToolContext Context(string username = "alice", IList<AssistantMediaSummary>? items = null) => new(
        1, username, false, new ServiceCollection().BuildServiceProvider(), CancellationToken.None,
        ContextItems: items);

    static AIFunction Tool(IAssistantTool pack, string name) =>
        (AIFunction)pack.CreateTools(Context()).Single(t => t.Name == name);

    [Test]
    public async Task SearchMedia_MergesSources_AndReturnsWireItems()
    {
        FakeSource tv = new("tv");
        FakeSource music = new("music");
        LibraryAssistantTool pack = new([tv, music], new WatchStateService([]));

        JsonElement json = (JsonElement)(await Tool(pack, "search_media").InvokeAsync(
            new AIFunctionArguments { ["query"] = "dune", ["domain"] = "", ["limit"] = 8 }))!;

        string text = json.GetRawText();
        await Assert.That(text).Contains("tv:item:1");
        await Assert.That(text).Contains("music:item:1");
        await Assert.That(tv.Queries.Count).IsEqualTo(1);
        await Assert.That(music.Queries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SearchMedia_HonoursTheDomainFilter()
    {
        FakeSource tv = new("tv");
        FakeSource music = new("music");
        LibraryAssistantTool pack = new([tv, music], new WatchStateService([]));

        await Tool(pack, "search_media").InvokeAsync(
            new AIFunctionArguments { ["query"] = "portishead", ["domain"] = "music", ["limit"] = 5 });

        await Assert.That(tv.Queries.Count).IsEqualTo(0);
        await Assert.That(music.Queries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SearchMedia_RemembersItemsForFollowUps()
    {
        FakeSource tv = new("tv");
        LibraryAssistantTool pack = new([tv], new WatchStateService([]));
        List<AssistantMediaSummary> items = [];
        AIFunction search = (AIFunction)pack.CreateTools(Context(items: items))
            .Single(t => t.Name == "search_media");

        await search.InvokeAsync(new AIFunctionArguments { ["query"] = "dune", ["limit"] = 5 });

        await Assert.That(items.Count).IsGreaterThan(0);
        await Assert.That(items[0].Id).StartsWith("tv:");
    }

    [Test]
    public async Task SearchMedia_PassesStructuredFiltersToTheSource()
    {
        FakeSource tv = new("tv");
        LibraryAssistantTool pack = new([tv], new WatchStateService([]));

        await Tool(pack, "search_media").InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "",
            ["domain"] = "anime",
            ["genre"] = "Action",
            ["person"] = "Tom Cruise",
            ["scope"] = "library",
            ["sort"] = "popularity",
            ["yearFrom"] = 2010,
            ["yearTo"] = 2025,
            ["limit"] = 10
        });

        AssistantMediaQuery query = tv.Queries.Single();
        await Assert.That(query.Text).IsEqualTo("");
        await Assert.That(query.Genre).IsEqualTo("Action");
        await Assert.That(query.Person).IsEqualTo("Tom Cruise");
        await Assert.That(query.Scope).IsEqualTo(AssistantMediaScope.Library);
        await Assert.That(query.Sort).IsEqualTo("popularity");
        await Assert.That(query.Anime).IsTrue();
        await Assert.That(query.YearFrom).IsEqualTo(2010);
        await Assert.That(query.YearTo).IsEqualTo(2025);
    }

    [Test]
    public async Task SearchMedia_RequiresTextOrAFilter()
    {
        FakeSource tv = new("tv");
        LibraryAssistantTool pack = new([tv], new WatchStateService([]));

        JsonElement json = (JsonElement)(await Tool(pack, "search_media").InvokeAsync(
            new AIFunctionArguments { ["query"] = "", ["domain"] = "" }))!;

        await Assert.That(json.GetRawText()).Contains("error");
        await Assert.That(tv.Queries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetMedia_RoutesByIdPrefix_AndPassesTheCaller()
    {
        FakeSource tv = new("tv");
        LibraryAssistantTool pack = new([tv], new WatchStateService([]));

        JsonElement json = (JsonElement)(await Tool(pack, "get_media").InvokeAsync(
            new AIFunctionArguments { ["id"] = "tv:item:1" }))!;
        string text = json.GetRawText();

        await Assert.That(tv.DetailIds).IsEquivalentTo(new[] { "tv:item:1" });
        await Assert.That(tv.Usernames).IsEquivalentTo(new string?[] { "alice" });
        await Assert.That(text).Contains("Overview");
        await Assert.That(text).Contains("Status: downloaded");
    }

    [Test]
    public async Task GetMedia_RejectsAnUnknownPrefix()
    {
        LibraryAssistantTool pack = new([new FakeSource("tv")], new WatchStateService([]));

        JsonElement json = (JsonElement)(await Tool(pack, "get_media").InvokeAsync(
            new AIFunctionArguments { ["id"] = "nope:1" }))!;

        await Assert.That(json.GetRawText()).Contains("error");
    }

    [Test]
    public async Task GetMedia_PagesLargeRelatedLists_WithoutSendingArtwork()
    {
        AssistantMediaSummary summary = new("tv", "tv:series:1", AssistantMediaKind.Series, "Long series",
            ArtUrl: "https://art.example/" + new string('x', 500));
        FakeSource source = new("tv")
        {
            Detail = new(summary, new string('o', 5000), ["Downloaded"],
                Enumerable.Range(1, 103).Select(i => summary with { Id = $"tv:episode:{i}", Title = $"Episode {i}" }).ToList())
        };
        LibraryAssistantTool pack = new([source], new WatchStateService([]));
        AIFunction tool = Tool(pack, "get_media");
        JsonElement first = (JsonElement)(await tool.InvokeAsync(new AIFunctionArguments { ["id"] = summary.Id }))!;
        await Assert.That(first.GetProperty("related").GetArrayLength()).IsEqualTo(8);
        await Assert.That(first.GetProperty("relatedCount").GetInt32()).IsEqualTo(103);
        await Assert.That(first.GetProperty("nextRelatedOffset").GetInt32()).IsEqualTo(8);
        await Assert.That(first.GetProperty("overview").GetString()!.Length).IsEqualTo(1501);
        await Assert.That(first.GetRawText()).DoesNotContain("https://art.example");

        JsonElement tail = (JsonElement)(await tool.InvokeAsync(new AIFunctionArguments
        {
            ["id"] = summary.Id, ["relatedOffset"] = 100, ["relatedLimit"] = 20
        }))!;
        await Assert.That(tail.GetProperty("related").GetArrayLength()).IsEqualTo(3);
        await Assert.That(tail.GetProperty("related")[0].GetProperty("id").GetString()).IsEqualTo("tv:episode:101");
        await Assert.That(tail.GetProperty("nextRelatedOffset").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task ContinueWatching_ExplainsWhenNoPlayerIsConfigured()
    {
        LibraryAssistantTool pack = new([new FakeSource("tv")], new WatchStateService([]));

        JsonElement json = (JsonElement)(await Tool(pack, "get_continue_watching").InvokeAsync(
            new AIFunctionArguments { ["limit"] = 5 }))!;

        await Assert.That(json.GetProperty("available").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task ContinueWatching_ReturnsThePlayersItems()
    {
        FakeWatchProvider provider = new();
        LibraryAssistantTool pack = new([new FakeSource("tv")], new WatchStateService([provider]));

        JsonElement json = (JsonElement)(await Tool(pack, "get_continue_watching").InvokeAsync(
            new AIFunctionArguments { ["limit"] = 5 }))!;
        string text = json.GetRawText();

        await Assert.That(text).Contains("Severance");
        await Assert.That(json.GetProperty("available").GetBoolean()).IsTrue();
        await Assert.That(provider.Username).IsEqualTo("alice");
    }

    sealed class FakeWatchProvider : IWatchStateProvider
    {
        public string Name => "Fake player";
        public bool IsAvailable => true;
        public string? Username { get; private set; }

        public Task<IReadOnlyDictionary<MediaKey, WatchState>> GetStatesAsync(
            string username, IReadOnlyCollection<MediaKey> keys, CancellationToken ct = default)
        {
            Username = username;
            return Task.FromResult<IReadOnlyDictionary<MediaKey, WatchState>>(new Dictionary<MediaKey, WatchState>());
        }

        public Task<IReadOnlyDictionary<int, SeriesWatchState>> GetSeriesStatesAsync(
            string username, IReadOnlyCollection<int> tmdbOrTvdbIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, SeriesWatchState>>(new Dictionary<int, SeriesWatchState>());

        public Task<IReadOnlyList<ContinueWatchingItem>> GetContinueWatchingAsync(
            string username, int limit = 20, CancellationToken ct = default)
        {
            Username = username;
            IReadOnlyList<ContinueWatchingItem> items =
            [
                new(new MediaKey(new MediaDomain("tv"), TmdbId: 1, SeasonNumber: 2, EpisodeNumber: 3),
                    "Severance", null, null, null, "/watch", "Severance", 0.4, 0, 0, DateTime.UtcNow)
            ];
            return Task.FromResult(items);
        }

        public Task<IReadOnlyList<WatchHistoryEntry>> GetHistoryAsync(
            string username, int limit = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WatchHistoryEntry>>([]);
    }
}
