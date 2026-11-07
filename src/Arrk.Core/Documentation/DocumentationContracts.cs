namespace Arrk.Core.Documentation;

/// <summary>Module-owned Markdown, identified by a module/stem key rather than a path or URL.</summary>
public sealed record DocumentationDocument(string Id, string Markdown);

/// <summary>Contributes trusted documentation without depending on any documentation consumer.</summary>
public interface IDocumentationProvider
{
    IReadOnlyList<DocumentationDocument> Documents { get; }
}

public sealed record DocumentationSection(string Id, string Title);
public sealed record DocumentationTopic(string Id, string Title, string Summary,
    IReadOnlyList<DocumentationSection> Sections);
public sealed record DocumentationMatch(string Topic, string Title, string Section,
    string SectionTitle, string Excerpt);
public sealed record DocumentationPage(string Topic, string Title, string Section,
    string Markdown, int? NextOffset, string? Error = null);
