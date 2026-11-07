using System.ComponentModel;
using System.Text.Json;
using Arrk.Core.Documentation;
using Microsoft.Extensions.AI;

namespace Arrk.Ai.Tools;

/// <summary>AI adapter over the same module documentation used by human readers.</summary>
public sealed class DocumentationAssistantTool(DocumentationCatalog documents) : IAssistantTool
{
    public string Key => "documentation";
    public string Name => "Documentation";
    public string Description => "Search and read curated Arrk instructions; not live instance state.";
    public AssistantAccess Access => AssistantAccess.User;
    public IReadOnlyList<string> Keywords => [];
    public bool IsAvailable => true;

    public IReadOnlyList<AITool> CreateTools(AssistantToolContext context) =>
    [
        AIFunctionFactory.Create(
            ([Description("Specific keywords; empty browses the installed documentation index.")] string query = "",
                [Description("Index continuation offset; ignored for a nonempty search.")] int offset = 0) =>
            {
                int start = Math.Clamp(offset, 0, documents.Topics.Count);
                return string.IsNullOrWhiteSpace(query)
                    ? JsonSerializer.Serialize(new
                    {
                        topics = documents.Topics.Skip(start).Take(8).Select(t => new { t.Id, t.Title, t.Summary }),
                        nextOffset = start + 8 < documents.Topics.Count ? (int?)(start + 8) : null
                    })
                    : JsonSerializer.Serialize(new { matches = documents.Search(query) });
            },
            "search_documentation",
            "Search installed module and plugin documentation, up to 8 excerpts. Empty query browses 8 topics at a time. Read a matching section next. Not live state."),
        AIFunctionFactory.Create(
            ([Description("Module-qualified topic id from search_documentation.")] string topic,
                [Description("Section id from the index/search; empty reads overview.")] string section = "",
                [Description("Continuation offset from a previous read; otherwise 0.")] int offset = 0) =>
                JsonSerializer.Serialize(new
                {
                    page = documents.Read(topic, section, offset),
                    sections = string.IsNullOrWhiteSpace(section)
                        ? documents.Topics.FirstOrDefault(t => t.Id == topic)?.Sections.Take(12) : null
                }),
            "read_documentation",
            "Read a documentation section, at most 4000 characters. Empty section reads the overview and first 12 section ids; search finds others. Page.NextOffset continues a long section. Not current state.")
    ];
}
