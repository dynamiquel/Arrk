using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Stackarr.Ai.Tests;

/// <summary>
/// Tool routing is the cheap, deterministic part of the assistant: it decides which packs are offered
/// without spending a model call. These pin access filtering, keyword routing, the schema cap and the
/// promise that a broken pack cannot take the turn down.
/// </summary>
public class AssistantRegistryTests
{
    static AssistantToolContext Context(bool admin = false) => new(
        1, "alice", admin, new ServiceCollection().BuildServiceProvider(), CancellationToken.None);

    [Test]
    public async Task AlwaysOnPacks_AreOffered_KeywordPacksOnlyWhenMentioned()
    {
        AssistantRegistry registry = new(
            [new TestPack("always"), new TestPack("downloads", ["download", "torrent"])],
            []);

        IReadOnlyList<string> general = registry.Build(Context(), "what is in my library?").PackKeys;
        IReadOnlyList<string> download = registry.Build(Context(), "why is my download stuck?").PackKeys;

        await Assert.That(general).IsEquivalentTo(new[] { "always" });
        await Assert.That(download).IsEquivalentTo(new[] { "always", "downloads" });
    }

    [Test]
    public async Task AdminPacks_AreOnlyOfferedToAdministrators()
    {
        AssistantRegistry registry = new([new TestPack("admin", access: AssistantAccess.Admin)], []);

        await Assert.That(registry.Build(Context(), "hi").PackKeys.Count).IsEqualTo(0);
        await Assert.That(registry.Build(Context(admin: true), "hi").PackKeys).IsEquivalentTo(new[] { "admin" });
    }

    [Test]
    public async Task UnavailablePacks_AreSkipped()
    {
        TestPack pack = new("offline") { Available = false };
        AssistantRegistry registry = new([pack], []);

        await Assert.That(registry.Build(Context(), "hi").Tools.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ABrokenPack_IsSkipped_WithoutFailingTheTurn()
    {
        AssistantRegistry registry = new([new TestPack("broken") { ThrowOnCreate = true }], []);

        AssistantToolSet set = registry.Build(Context(), "hi");

        await Assert.That(set.Tools.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ToolSchemas_AreCapped()
    {
        AssistantRegistry registry = new([new TestPack("many", toolCount: AssistantRegistry.MaxTools + 5)], []);

        AssistantToolSet set = registry.Build(Context(), "hi");

        await Assert.That(set.Tools.Count).IsEqualTo(AssistantRegistry.MaxTools);
    }

    [Test]
    public async Task MediaSources_AreExposedOnlyWhenAvailable()
    {
        AssistantRegistry registry = new([], [new FakeSource("tv"), new FakeSource("music") { Available = false }]);

        await Assert.That(registry.AvailableMediaSources.Select(s => s.Key).ToList()).IsEquivalentTo(new[] { "tv" });
    }

    sealed class TestPack : IAssistantTool
    {
        public TestPack(string key, IReadOnlyList<string>? keywords = null, AssistantAccess access = AssistantAccess.User,
            int toolCount = 1)
        {
            Key = key;
            Keywords = keywords ?? [];
            Access = access;
            ToolCount = toolCount;
        }

        public string Key { get; }
        public string Name => Key;
        public string Description => "";
        public AssistantAccess Access { get; }
        public IReadOnlyList<string> Keywords { get; }
        public bool Available { get; set; } = true;
        public bool ThrowOnCreate { get; set; }
        public int ToolCount { get; }
        public bool IsAvailable => Available;

        public IReadOnlyList<AITool> CreateTools(AssistantToolContext context)
        {
            if (ThrowOnCreate)
                throw new InvalidOperationException("broken pack");

            return Enumerable.Range(0, ToolCount)
                .Select(i => (AITool)AIFunctionFactory.Create(
                    () => "ok", $"{Key}_tool_{i}", "test tool"))
                .ToList();
        }
    }
}

/// <summary>A media source with no I/O, used by the registry and library-pack tests.</summary>
public sealed class FakeSource(string key) : IAssistantMediaSource
{
    public string Key { get; } = key;
    public string DisplayName => Key;
    public bool Available { get; set; } = true;
    public bool IsAvailable => Available;
    public List<AssistantMediaQuery> Queries { get; } = [];
    public List<string> DetailIds { get; } = [];
    public List<string?> Usernames { get; } = [];

    public Task<IReadOnlyList<AssistantMediaSummary>> SearchAsync(AssistantMediaQuery query, CancellationToken ct)
    {
        Queries.Add(query);
        IReadOnlyList<AssistantMediaSummary> results =
        [
            new(Key, $"{Key}:item:1", AssistantMediaKind.Movie, $"{Key} result", "Item", 2020, "In library")
        ];
        return Task.FromResult(results);
    }

    public Task<AssistantMediaDetail?> GetAsync(string id, string? username, CancellationToken ct)
    {
        DetailIds.Add(id);
        Usernames.Add(username);
        AssistantMediaDetail detail = new(
            new AssistantMediaSummary(Key, id, AssistantMediaKind.Movie, "Detail", "Item", 2020, "In library"),
            "Overview", ["Status: downloaded"], []);
        return Task.FromResult<AssistantMediaDetail?>(detail);
    }
}
