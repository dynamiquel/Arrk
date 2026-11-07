using Arrk.Core.Documentation;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Arrk.App.Services.Ui;

/// <summary>Renders installed guides, never raw HTML or executable link destinations.</summary>
public static class DocumentationMarkdown
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .Build();

    public static string Render(string markdown, IReadOnlyList<DocumentationSection>? sections = null)
    {
        MarkdownDocument document = Markdown.Parse(markdown, Pipeline);
        if (sections is not null)
        {
            // Use catalog ids rather than a second Markdown slug algorithm.
            foreach ((HeadingBlock heading, DocumentationSection section) in document.Descendants<HeadingBlock>()
                .Where(h => h.Level == 2 && h.Column == 0 && markdown.AsSpan(h.Span.Start).StartsWith("## "))
                .Zip(sections.Skip(1)))
                heading.GetAttributes().Id = "guide-" + section.Id;
        }

        foreach (LinkInline link in document.Descendants<LinkInline>())
        {
            if (!IsSafeDestination(link.Url, link.IsImage))
                link.Url = "#";
        }

        foreach (AutolinkInline link in document.Descendants<AutolinkInline>())
        {
            if (!IsSafeDestination(link.Url, image: false))
                link.Url = "#";
        }

        return document.ToHtml(Pipeline);
    }

    public static string Preview(string markdown)
    {
        // The result already displays the section title separately.
        MarkdownDocument document = Markdown.Parse(markdown, Pipeline);
        if (document.FirstOrDefault() is HeadingBlock heading)
            markdown = markdown[(heading.Span.End + 1)..];

        string text = System.Text.RegularExpressions.Regex.Replace(Markdown.ToPlainText(markdown, Pipeline), @"\s+", " ").Trim();
        if (text.Length <= 240)
            return text;

        int end = text.LastIndexOf(' ', 239, 240);
        return text[..(end > 0 ? end : 240)].TrimEnd() + "…";
    }

    static bool IsSafeDestination(string? destination, bool image)
    {
        if (string.IsNullOrWhiteSpace(destination))
            return true;

        string url = destination.Trim();
        if (url.Any(char.IsControl) || url.Contains('\\') || url.StartsWith("//", StringComparison.Ordinal))
            return false;

        // Uri treats root-relative paths as absolute file URIs; browsers treat them as local routes.
        if (url.StartsWith('/'))
            return true;

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? absolute))
            return absolute.Scheme is "https" or "http" || (!image && absolute.Scheme == "mailto");

        // A colon before the first path/query/fragment separator is a scheme, not a local path.
        return !url.Split('/', '?', '#')[0].Contains(':');
    }
}
