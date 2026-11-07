using System.Text.Json;
using Arrk.Ai.Tools;
using Arrk.Core.Documentation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Arrk.Ai.Tests;

public class DocumentationAssistantToolTests
{
    sealed class PluginDocumentation : IDocumentationProvider
    {
        public IReadOnlyList<DocumentationDocument> Documents { get; } = Enumerable.Range(0, 10)
            .Select(i => new DocumentationDocument($"plugin/doc-{i}", $"# Plugin topic {i}\n\nUser instructions.\n\n"
                + (i == 9 ? "## Headphones\nConnect your headphones before playing.\n" : "## Start\nOpen the plugin page.\n")))
            .ToArray();
    }

    [Test]
    public async Task Tools_BrowseBoundedIndex_SearchAndReadPluginDocs_WithoutProviderCalls()
    {
        DocumentationAssistantTool pack = new(new DocumentationCatalog([new PluginDocumentation()]));
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        AssistantToolContext context = new(1, "alice", false, services, CancellationToken.None);
        AssistantToolSet set = new AssistantRegistry([pack], []).Build(context, "How does this work?");
        await Assert.That(set.Tools.Count).IsEqualTo(2);
        AIFunction search = (AIFunction)set.Tools.Single(t => t.Name == "search_documentation");
        using JsonDocument first = await Invoke(search, new());
        await Assert.That(first.RootElement.GetProperty("topics").GetArrayLength()).IsEqualTo(8);
        await Assert.That(first.RootElement.GetProperty("nextOffset").GetInt32()).IsEqualTo(8);
        using JsonDocument last = await Invoke(search, new() { ["offset"] = 8 });
        await Assert.That(last.RootElement.GetProperty("topics").GetArrayLength()).IsEqualTo(2);
        await Assert.That(last.RootElement.GetProperty("nextOffset").ValueKind).IsEqualTo(JsonValueKind.Null);

        using JsonDocument matches = await Invoke(search, new() { ["query"] = "headphones" });
        JsonElement match = matches.RootElement.GetProperty("matches")[0];
        await Assert.That(match.GetProperty("Topic").GetString()).IsEqualTo("plugin/doc-9");
        await Assert.That(match.GetProperty("Section").GetString()).IsEqualTo("headphones");
        AIFunction read = (AIFunction)set.Tools.Single(t => t.Name == "read_documentation");
        using JsonDocument page = await Invoke(read, new() { ["topic"] = "plugin/doc-9", ["section"] = "headphones" });
        await Assert.That(page.RootElement.GetProperty("page").GetProperty("Markdown").GetString())
            .Contains("Connect your headphones before playing.");
        using JsonDocument overview = await Invoke(read, new() { ["topic"] = "plugin/doc-9" });
        await Assert.That(overview.RootElement.GetProperty("sections")[1].GetProperty("Id").GetString())
            .IsEqualTo("headphones");
    }

    static async Task<JsonDocument> Invoke(AIFunction function, AIFunctionArguments arguments)
    {
        JsonElement result = (JsonElement)(await function.InvokeAsync(arguments))!;
        return JsonDocument.Parse(result.GetString()!);
    }
}
